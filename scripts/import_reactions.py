#!/usr/bin/env python3
"""Import available Telegram reactions into local MyTelegram stack.

What the script does:
1. Fetches reactions via messages.getAvailableReactions.
2. Downloads referenced reaction media documents.
3. Uploads media to MinIO (bucket object name = DocumentId).
4. Upserts documents into eventflow-documentreadmodel.
5. Upserts reactions into eventflow-reactionreadmodel.
6. Optionally writes DataSeeder-compatible JSON files:
   downloads/reactions/reactions.data.json
   downloads/reactions/reactions.documents.json
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import json
import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from bson import Int64
from dotenv import load_dotenv
from minio import Minio
from minio.error import S3Error
from pymongo import MongoClient
from pymongo.collection import Collection
from telethon import TelegramClient, functions
from telethon.errors import RPCError


DEFAULT_SESSION = "reaction_importer_session"
DEFAULT_MINIO_BUCKET = "tg-files"
DEFAULT_REACTIONS_COLLECTION = "eventflow-reactionreadmodel"
DEFAULT_DOCUMENTS_COLLECTION = "eventflow-documentreadmodel"
DEFAULT_DB_NAME = "tg"
DEFAULT_MONGO_URI = "mongodb://localhost:27017"
DEFAULT_MINIO_ENDPOINT = "localhost:9000"


@dataclass
class PreparedDocument:
    document_id: int
    dc_id: int
    mime_type: str
    size: int
    file_reference_b64: str | None


def _env(name: str, default: str | None = None) -> str | None:
    value = os.getenv(name)
    if value is None or value == "":
        return default
    return value


def _normalize_mongo_uri(uri: str) -> str:
    if "mongodb://mongodb:" in uri:
        return uri.replace("mongodb://mongodb:", "mongodb://localhost:", 1)
    return uri


def _normalize_endpoint(endpoint: str) -> str:
    endpoint = endpoint.strip()
    if endpoint.startswith("http://"):
        endpoint = endpoint[len("http://") :]
    if endpoint.startswith("https://"):
        endpoint = endpoint[len("https://") :]
    if endpoint.startswith("minio:"):
        endpoint = endpoint.replace("minio:", "localhost:", 1)
    return endpoint


def _to_int(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def reaction_id_from_emoji(emoji: str) -> int:
    """
    Matches C# Reaction.GetReactionId() logic:
    - UTF8 bytes
    - first 8 bytes (or zero-padded to 8)
    - little-endian Int64
    """
    raw = emoji.encode("utf-8")
    if len(raw) >= 8:
        chunk = raw[:8]
    else:
        chunk = raw + (b"\x00" * (8 - len(raw)))
    return int.from_bytes(chunk, byteorder="little", signed=True)


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Import Telegram reactions to local MyTelegram DB/MinIO")

    p.add_argument("--api-id", type=int, help="Telegram API ID (overrides .env)")
    p.add_argument("--api-hash", help="Telegram API hash (overrides .env)")
    p.add_argument("--session", default=DEFAULT_SESSION, help="Telethon session name")
    p.add_argument("--env-file", default=None, help="Path to .env (default: ../.env)")

    p.add_argument("--mongo-uri", default=None, help="Mongo URI")
    p.add_argument("--mongo-db", default=None, help="Mongo DB name")
    p.add_argument("--reactions-collection", default=DEFAULT_REACTIONS_COLLECTION, help="Mongo reactions collection")
    p.add_argument("--documents-collection", default=DEFAULT_DOCUMENTS_COLLECTION, help="Mongo documents collection")

    p.add_argument("--minio-endpoint", default=None, help="MinIO endpoint host:port")
    p.add_argument("--minio-access-key", default=None, help="MinIO access key")
    p.add_argument("--minio-secret-key", default=None, help="MinIO secret key")
    p.add_argument("--minio-bucket", default=DEFAULT_MINIO_BUCKET, help="MinIO bucket name")
    p.add_argument("--minio-secure", action="store_true", help="Use HTTPS for MinIO")

    p.add_argument("--skip-upload", action="store_true", help="Skip MinIO upload")
    p.add_argument("--skip-documents", action="store_true", help="Skip document upsert in Mongo")
    p.add_argument("--skip-reactions", action="store_true", help="Skip reaction upsert in Mongo")
    p.add_argument("--only-reaction", default=None, help="Import only one emoji reaction, e.g. :thumbs_up:")
    p.add_argument("--write-seed-json", action="store_true", help="Write downloads/reactions/*.json")
    p.add_argument("--seed-dir", default="downloads/reactions", help="Seed output directory")
    p.add_argument("--dry-run", action="store_true", help="Do not write Mongo/MinIO/JSON")

    return p.parse_args()


def get_env_and_validate(args: argparse.Namespace) -> dict[str, Any]:
    env_path = Path(args.env_file).expanduser() if args.env_file else (Path(__file__).resolve().parent.parent / ".env")
    if env_path.exists():
        load_dotenv(env_path)

    api_id_raw = args.api_id if args.api_id is not None else (_env("TELEGRAM_API_ID") or _env("TG_API_ID"))
    api_hash = args.api_hash or _env("TELEGRAM_API_HASH") or _env("TG_API_HASH")
    if api_id_raw is None or not api_hash:
        raise ValueError("Telegram API credentials are missing. Set --api-id/--api-hash or TELEGRAM_API_ID/TELEGRAM_API_HASH.")

    try:
        api_id = int(api_id_raw)
    except (TypeError, ValueError) as e:
        raise ValueError("TELEGRAM_API_ID must be integer.") from e

    mongo_uri = _normalize_mongo_uri(args.mongo_uri or _env("ConnectionStrings__Default", DEFAULT_MONGO_URI))
    mongo_db = args.mongo_db or _env("App__ReadModelDatabaseName") or _env("App__DatabaseName", DEFAULT_DB_NAME)

    minio_endpoint = _normalize_endpoint(args.minio_endpoint or _env("Minio__Endpoint", DEFAULT_MINIO_ENDPOINT))
    minio_access_key = args.minio_access_key or _env("Minio__AccessKey")
    minio_secret_key = args.minio_secret_key or _env("Minio__SecretKey")

    if not args.skip_upload and (not minio_access_key or not minio_secret_key):
        raise ValueError("MinIO credentials are required unless --skip-upload is set.")

    return {
        "api_id": api_id,
        "api_hash": api_hash,
        "mongo_uri": mongo_uri,
        "mongo_db": mongo_db,
        "minio_endpoint": minio_endpoint,
        "minio_access_key": minio_access_key,
        "minio_secret_key": minio_secret_key,
    }


def connect_mongo(mongo_uri: str, mongo_db: str) -> tuple[MongoClient, Any]:
    mongo_client = MongoClient(mongo_uri, serverSelectionTimeoutMS=5000)
    mongo_client.server_info()
    db = mongo_client[mongo_db]
    return mongo_client, db


def connect_minio(
    endpoint: str,
    access_key: str,
    secret_key: str,
    bucket: str,
    secure: bool,
) -> Minio:
    minio_client = Minio(
        endpoint=endpoint,
        access_key=access_key,
        secret_key=secret_key,
        secure=secure,
    )
    if not minio_client.bucket_exists(bucket):
        minio_client.make_bucket(bucket)
    return minio_client


async def fetch_available_reactions(client: TelegramClient) -> list[Any]:
    result = await client(functions.messages.GetAvailableReactionsRequest(hash=0))
    reactions = list(getattr(result, "reactions", []) or [])
    return reactions


def get_document_id(document: Any) -> int | None:
    if document is None:
        return None
    doc_id = getattr(document, "id", None)
    if doc_id is None:
        return None
    return _to_int(doc_id, 0) or None


def file_ref_base64(document: Any) -> str | None:
    ref = getattr(document, "file_reference", None)
    if not ref:
        return None
    try:
        return base64.b64encode(bytes(ref)).decode("ascii")
    except Exception:
        return None


def build_document_mongo_doc(meta: PreparedDocument) -> dict[str, Any]:
    return {
        "_id": f"document-{meta.document_id}",
        "DocumentId": Int64(meta.document_id),
        "DcId": meta.dc_id,
        "MimeType": meta.mime_type,
        "Size": Int64(meta.size),
        "Version": Int64(1),
        "FileReference": base64.b64decode(meta.file_reference_b64) if meta.file_reference_b64 else None,
    }


def build_document_seed_item(meta: PreparedDocument) -> dict[str, Any]:
    return {
        "id": meta.document_id,
        "accessHash": 0,
        "dcId": meta.dc_id,
        "date": int(time.time()),
        "mimeType": meta.mime_type,
        "size": meta.size,
        "fileReference": meta.file_reference_b64 or "",
        "name": None,
        "creatorId": None,
        "thumbId": None,
        "videoThumbId": None,
        "md5CheckSum": None,
        "thumbs": None,
        "videoThumbs": None,
        "fingerprint": None,
        "attributes2": None,
    }


async def import_document(
    client: TelegramClient,
    minio_client: Minio | None,
    minio_bucket: str,
    documents_collection: Collection | None,
    document: Any,
    dry_run: bool,
) -> PreparedDocument | None:
    if document is None:
        return None

    doc_id = get_document_id(document)
    if doc_id is None:
        return None

    mime_type = str(getattr(document, "mime_type", None) or "application/octet-stream")
    dc_id = _to_int(getattr(document, "dc_id", None), 2)
    file_reference_b64 = file_ref_base64(document)

    existing_size = 0
    if documents_collection is not None:
        existing = documents_collection.find_one({"_id": f"document-{doc_id}"}, {"Size": 1})
        if existing and "Size" in existing and existing["Size"] is not None:
            existing_size = _to_int(existing["Size"], 0)

    file_bytes = await client.download_media(document, file=bytes)
    if not file_bytes:
        raise RuntimeError(f"Failed to download document {doc_id}")
    size = len(file_bytes)

    if dry_run:
        return PreparedDocument(
            document_id=doc_id,
            dc_id=dc_id,
            mime_type=mime_type,
            size=size,
            file_reference_b64=file_reference_b64,
        )

    if minio_client is not None:
        minio_client.put_object(
            bucket_name=minio_bucket,
            object_name=str(doc_id),
            data=MemoryBytesIO(file_bytes),
            length=size,
            content_type=mime_type,
        )

    if documents_collection is not None:
        doc_data = build_document_mongo_doc(
            PreparedDocument(
                document_id=doc_id,
                dc_id=dc_id,
                mime_type=mime_type,
                size=size if size > 0 else existing_size,
                file_reference_b64=file_reference_b64,
            )
        )
        documents_collection.replace_one({"_id": doc_data["_id"]}, doc_data, upsert=True)

    return PreparedDocument(
        document_id=doc_id,
        dc_id=dc_id,
        mime_type=mime_type,
        size=size if size > 0 else existing_size,
        file_reference_b64=file_reference_b64,
    )


def reaction_to_doc_refs(reaction: Any) -> dict[str, Any]:
    return {
        "static": getattr(reaction, "static_icon", None),
        "appear": getattr(reaction, "appear_animation", None),
        "select": getattr(reaction, "select_animation", None),
        "activate": getattr(reaction, "activate_animation", None),
        "effect": getattr(reaction, "effect_animation", None),
        "around": getattr(reaction, "around_animation", None),
        "center": getattr(reaction, "center_icon", None),
    }


def build_reaction_doc(reaction: Any, refs: dict[str, int | None]) -> dict[str, Any]:
    emoji = str(getattr(reaction, "reaction", "") or "")
    rid = reaction_id_from_emoji(emoji) if emoji else 0
    return {
        "_id": f"reaction-{rid}",
        "ReactionId": Int64(rid),
        "Reaction": emoji,
        "Title": str(getattr(reaction, "title", "") or ""),
        "StaticIconId": Int64(refs["static"]) if refs["static"] is not None else Int64(0),
        "AppearAnimationId": Int64(refs["appear"]) if refs["appear"] is not None else Int64(0),
        "SelectAnimationId": Int64(refs["select"]) if refs["select"] is not None else Int64(0),
        "ActivateAnimationId": Int64(refs["activate"]) if refs["activate"] is not None else Int64(0),
        "EffectAnimationId": Int64(refs["effect"]) if refs["effect"] is not None else Int64(0),
        "AroundAnimationId": Int64(refs["around"]) if refs["around"] is not None else None,
        "CenterIcon": Int64(refs["center"]) if refs["center"] is not None else None,
        "Inactive": bool(getattr(reaction, "inactive", False)),
        "Premium": bool(getattr(reaction, "premium", False)),
        "Version": Int64(1),
    }


def build_reaction_seed_item(reaction_doc: dict[str, Any]) -> dict[str, Any]:
    return {
        "reaction": reaction_doc["Reaction"],
        "title": reaction_doc["Title"],
        "staticIconId": int(reaction_doc["StaticIconId"]),
        "appearAnimationId": int(reaction_doc["AppearAnimationId"]),
        "selectAnimationId": int(reaction_doc["SelectAnimationId"]),
        "activateAnimationId": int(reaction_doc["ActivateAnimationId"]),
        "effectAnimationId": int(reaction_doc["EffectAnimationId"]),
        "aroundAnimationId": int(reaction_doc["AroundAnimationId"]) if reaction_doc["AroundAnimationId"] is not None else None,
        "centerIcon": int(reaction_doc["CenterIcon"]) if reaction_doc["CenterIcon"] is not None else None,
    }


def ensure_indexes(reactions_collection: Collection, documents_collection: Collection | None) -> None:
    reactions_collection.create_index("ReactionId", unique=True)
    reactions_collection.create_index("Reaction", unique=True)
    if documents_collection is not None:
        documents_collection.create_index("DocumentId", unique=True)


def write_seed_files(seed_dir: Path, reactions: list[dict[str, Any]], documents: list[PreparedDocument]) -> None:
    seed_dir.mkdir(parents=True, exist_ok=True)
    reactions_path = seed_dir / "reactions.data.json"
    documents_path = seed_dir / "reactions.documents.json"

    reaction_items = [build_reaction_seed_item(r) for r in reactions]
    document_items = [build_document_seed_item(d) for d in documents]

    reactions_path.write_text(json.dumps(reaction_items, ensure_ascii=False, indent=2), encoding="utf-8")
    documents_path.write_text(json.dumps(document_items, ensure_ascii=False, indent=2), encoding="utf-8")


class MemoryBytesIO:
    """Tiny file-like object for MinIO put_object without importing io.BytesIO."""

    def __init__(self, data: bytes):
        self._data = data
        self._offset = 0

    def read(self, n: int = -1) -> bytes:
        if n is None or n < 0:
            n = len(self._data) - self._offset
        if self._offset >= len(self._data):
            return b""
        chunk = self._data[self._offset : self._offset + n]
        self._offset += len(chunk)
        return chunk


async def run() -> int:
    args = parse_args()

    try:
        cfg = get_env_and_validate(args)
    except ValueError as e:
        print(f"ERROR: {e}")
        return 1

    mongo_client = None
    db = None
    reactions_collection = None
    documents_collection = None
    minio_client = None

    if not args.dry_run:
        try:
            mongo_client, db = connect_mongo(cfg["mongo_uri"], cfg["mongo_db"])
            if not args.skip_reactions:
                reactions_collection = db[args.reactions_collection]
            if not args.skip_documents:
                documents_collection = db[args.documents_collection]
            if not args.skip_upload:
                minio_client = connect_minio(
                    endpoint=cfg["minio_endpoint"],
                    access_key=str(cfg["minio_access_key"]),
                    secret_key=str(cfg["minio_secret_key"]),
                    bucket=args.minio_bucket,
                    secure=args.minio_secure,
                )
            if reactions_collection is not None:
                ensure_indexes(reactions_collection, documents_collection)
        except Exception as e:
            print(f"ERROR: DB/MinIO connect failed: {e}")
            return 1

    prepared_documents: dict[int, PreparedDocument] = {}
    reaction_docs: list[dict[str, Any]] = []

    try:
        async with TelegramClient(args.session, cfg["api_id"], cfg["api_hash"]) as client:
            me = await client.get_me()
            print(f"Authorized: {getattr(me, 'username', None) or me.id} (id={me.id})")

            reactions = await fetch_available_reactions(client)
            if args.only_reaction:
                reactions = [r for r in reactions if str(getattr(r, "reaction", "")) == args.only_reaction]

            print(f"Fetched reactions: {len(reactions)}")
            if not reactions:
                print("Nothing to import.")
                return 0

            for idx, reaction in enumerate(reactions, start=1):
                emoji = str(getattr(reaction, "reaction", "") or "")
                title = str(getattr(reaction, "title", "") or "")
                print(f"[{idx}/{len(reactions)}] {emoji} {title}")

                raw_refs = reaction_to_doc_refs(reaction)
                id_refs: dict[str, int | None] = {}

                for key, doc in raw_refs.items():
                    doc_id = get_document_id(doc)
                    id_refs[key] = doc_id
                    if doc_id is None:
                        continue
                    if doc_id in prepared_documents:
                        continue

                    try:
                        prepared = await import_document(
                            client=client,
                            minio_client=minio_client,
                            minio_bucket=args.minio_bucket,
                            documents_collection=documents_collection,
                            document=doc,
                            dry_run=args.dry_run,
                        )
                        if prepared:
                            prepared_documents[prepared.document_id] = prepared
                    except RPCError as e:
                        print(f"  WARN document {doc_id} RPC error: {e}")
                    except S3Error as e:
                        print(f"  WARN document {doc_id} MinIO error: {e}")
                    except Exception as e:
                        print(f"  WARN document {doc_id} error: {e}")

                reaction_doc = build_reaction_doc(reaction, id_refs)
                reaction_docs.append(reaction_doc)

                if reactions_collection is not None and not args.dry_run:
                    reactions_collection.replace_one(
                        {"_id": reaction_doc["_id"]},
                        reaction_doc,
                        upsert=True,
                    )

    finally:
        if mongo_client is not None:
            mongo_client.close()

    if args.write_seed_json:
        if args.dry_run:
            print("Dry run: seed files were not written.")
        else:
            write_seed_files(Path(args.seed_dir), reaction_docs, list(prepared_documents.values()))
            print(f"Seed JSON written: {args.seed_dir}")

    print(
        "Done. "
        f"Reactions: {len(reaction_docs)}, "
        f"Documents: {len(prepared_documents)}, "
        f"DryRun: {args.dry_run}"
    )
    return 0


def main() -> int:
    try:
        return asyncio.run(run())
    except KeyboardInterrupt:
        print("Cancelled.")
        return 130


if __name__ == "__main__":
    sys.exit(main())
