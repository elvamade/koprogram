#!/usr/bin/env python3
"""
Telegram Star Gifts Importer
РРјРїРѕСЂС‚РёСЂСѓРµС‚ РїРѕРґР°СЂРєРё Рё Р°С‚СЂРёР±СѓС‚С‹ СѓР»СѓС‡С€РµРЅРёР№ РёР· РѕС„РёС†РёР°Р»СЊРЅРѕРіРѕ Telegram РІ Р»РѕРєР°Р»СЊРЅСѓСЋ Р±Р°Р·Сѓ MyTelegram.

РўСЂРµР±РѕРІР°РЅРёСЏ:
    pip install telethon pymongo minio python-dotenv

РСЃРїРѕР»СЊР·РѕРІР°РЅРёРµ:
    1. Р”РѕР±Р°РІСЊ РІ .env: TELEGRAM_API_ID Рё TELEGRAM_API_HASH
    2. Р—Р°РїСѓСЃС‚Рё: python scripts/import_gifts.py
"""

import asyncio
import base64
import hashlib
import os
import random
import subprocess
import sys
import time
from datetime import datetime
from io import BytesIO
from pathlib import Path
from typing import Dict, List, Optional
from urllib.parse import parse_qs, urlparse

from dotenv import load_dotenv

base_dir = Path(__file__).resolve().parent
for candidate in (base_dir / '.env', base_dir.parent / '.env'):
    if candidate.exists():
        load_dotenv(candidate, override=True)
        break
else:
    load_dotenv(override=True)

try:
    from telethon import TelegramClient
    from telethon.tl.functions.messages import GetStickerSetRequest
    from telethon.tl.functions.payments import GetStarGiftsRequest, GetStarGiftUpgradePreviewRequest
    from telethon.tl.types import InputStickerSetShortName
except ImportError:
    print("вќЊ Telethon РЅРµ СѓСЃС‚Р°РЅРѕРІР»РµРЅ: pip install telethon")
    sys.exit(1)

try:
    from pymongo import MongoClient
    from bson import Binary
except ImportError:
    print("вќЊ PyMongo РЅРµ СѓСЃС‚Р°РЅРѕРІР»РµРЅ: pip install pymongo")
    sys.exit(1)

try:
    from minio import Minio
except ImportError:
    print("вќЊ Minio РЅРµ СѓСЃС‚Р°РЅРѕРІР»РµРЅ: pip install minio")
    sys.exit(1)

# ============== РљРћРќР¤РР“РЈР РђР¦РРЇ ==============
def env_or_default(*keys: str, default: str = '') -> str:
    for key in keys:
        value = os.getenv(key)
        if value and value.strip():
            return value
    return default


API_ID = os.getenv('TELEGRAM_API_ID', '')
API_HASH = os.getenv('TELEGRAM_API_HASH', '')
SESSION_NAME = 'gift_importer_session'

MONGO_URI = os.getenv('ConnectionStrings__Default', 'mongodb://localhost:27017')
if 'mongodb:27017' in MONGO_URI:
    MONGO_URI = MONGO_URI.replace('mongodb:27017', 'localhost:27017')
DB_NAME = os.getenv('App__ReadModelDatabaseName', 'tg')

MINIO_ENDPOINT = env_or_default('Minio__Endpoint', 'MINIO_ENDPOINT', default='localhost:9000')
if 'minio:9000' in MINIO_ENDPOINT:
    MINIO_ENDPOINT = MINIO_ENDPOINT.replace('minio:9000', 'localhost:9000')
MINIO_ACCESS_KEY = env_or_default('Minio__AccessKey', 'MINIO_ROOT_USER', default='test')
MINIO_SECRET_KEY = env_or_default('Minio__SecretKey', 'MINIO_ROOT_PASSWORD', default='')
MINIO_BUCKET = 'tg-files'
MINIO_SECURE = False
TG_DC_ID = 2
TG_DC_IP = "149.154.167.40"
TG_DC_PORT = 443
MY_DC_ID = TG_DC_ID

# РљРѕР»Р»РµРєС†РёРё
GIFTS_COLLECTION = 'eventflow-stargiftreadmodel'
DOCUMENTS_COLLECTION = 'eventflow-documentreadmodel'
MODELS_COLLECTION = 'stargift_upgrade_models'
PATTERNS_COLLECTION = 'stargift_upgrade_patterns'
BACKDROPS_COLLECTION = 'stargift_upgrade_backdrops'
UPGRADE_COUNTERS_COLLECTION = 'stargift_upgrade_counters'


def calculate_convert_stars(stars: int) -> int:
    return int(stars * 0.85)


class GiftImporter:
    def __init__(self):
        self.client: Optional[TelegramClient] = None
        self.mongo_client: Optional[MongoClient] = None
        self.minio_client: Optional[Minio] = None
        self.db = None
        self.document_id_counter = None
        self.gifts_from_telegram = []

    async def connect_telegram(self) -> bool:
        if not API_ID or not API_HASH:
            print("\nвљ пёЏ  Telegram API credentials РЅРµ РЅР°СЃС‚СЂРѕРµРЅС‹!")
            print("Р”РѕР±Р°РІСЊС‚Рµ РІ .env: TELEGRAM_API_ID Рё TELEGRAM_API_HASH")
            return False
        self.client = TelegramClient(SESSION_NAME, int(API_ID), API_HASH)
        self.client.session.set_dc(TG_DC_ID, TG_DC_IP, TG_DC_PORT)
        await self.client.start()
        me = await self.client.get_me()
        print(f"вњ… Telegram: {me.first_name} (@{me.username})")
        return True

    def connect_mongodb(self) -> bool:
        try:
            self.mongo_client = MongoClient(MONGO_URI, serverSelectionTimeoutMS=5000)
            self.mongo_client.server_info()
            self.db = self.mongo_client[DB_NAME]
            print(f"вњ… MongoDB: {MONGO_URI}")
            self._init_document_id_counter()
            self._create_indexes()
            return True
        except Exception as e:
            print(f"вќЊ MongoDB РѕС€РёР±РєР°: {e}")
            return False

    def _init_document_id_counter(self):
        col = self.db[DOCUMENTS_COLLECTION]
        max_doc = col.find_one(sort=[("DocumentId", -1)])
        if max_doc and "DocumentId" in max_doc:
            self.document_id_counter = max_doc["DocumentId"] + 1
        else:
            self.document_id_counter = 5000000000000000000
        print(f"   рџ“Љ DocumentId: {self.document_id_counter}")

    def _create_indexes(self):
        self.db[MODELS_COLLECTION].create_index("GiftId")
        self.db[PATTERNS_COLLECTION].create_index("GiftId")
        self.db[BACKDROPS_COLLECTION].create_index("GiftId")
        self.db[UPGRADE_COUNTERS_COLLECTION].create_index("GiftId", unique=True)

    def _get_next_document_id(self) -> int:
        doc_id = self.document_id_counter
        self.document_id_counter += 1
        return doc_id

    def _generate_access_hash(self) -> int:
        return random.randint(1000000000000000000, 9223372036854775807)

    def connect_minio(self) -> bool:
        try:
            self.minio_client = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY,
                                      secret_key=MINIO_SECRET_KEY, secure=MINIO_SECURE)
            if not self.minio_client.bucket_exists(MINIO_BUCKET):
                self.minio_client.make_bucket(MINIO_BUCKET)
            print(f"вњ… MinIO: {MINIO_ENDPOINT}")
            return True
        except Exception as e:
            print(f"вќЊ MinIO РѕС€РёР±РєР°: {e}")
            return False

    async def download_and_upload_document(self, document) -> Optional[Dict]:
        if document is None:
            return None
        try:
            file_data = await self.client.download_media(document, file=bytes)
            mime_type = getattr(document, 'mime_type', 'application/x-tgsticker')
            if not file_data:
                return None
            new_doc_id = self._get_next_document_id()
            new_access_hash = self._generate_access_hash()
            if self.minio_client:
                self.minio_client.put_object(MINIO_BUCKET, str(new_doc_id),
                    BytesIO(file_data), length=len(file_data), content_type=mime_type)
            file_reference = hashlib.sha256(f"{new_doc_id}{time.time()}".encode()).digest()[:16]
            doc_data = {
                "DocumentId": new_doc_id, "AccessHash": new_access_hash,
                "FileReferenceBase64": base64.b64encode(file_reference).decode('ascii'),
                "Date": int(time.time()), "DcId": MY_DC_ID,
                "MimeType": mime_type, "Size": len(file_data)
            }
            self._save_document_to_mongo(doc_data)
            return doc_data
        except Exception as e:
            print(f"      вњ— РћС€РёР±РєР°: {e}")
            return None

    def _save_document_to_mongo(self, doc_data: Dict):
        col = self.db[DOCUMENTS_COLLECTION]
        mongo_doc = {
            "_id": f"document-{doc_data['DocumentId']}",
            "DocumentId": doc_data["DocumentId"], "AccessHash": doc_data["AccessHash"],
            "FileReference": Binary(base64.b64decode(doc_data["FileReferenceBase64"])),
            "Date": doc_data["Date"], "DcId": doc_data["DcId"],
            "MimeType": doc_data["MimeType"], "Size": doc_data["Size"],
            "CreatedAt": datetime.utcnow()
        }
        col.replace_one({"_id": mongo_doc["_id"]}, mongo_doc, upsert=True)

    async def fetch_gifts(self) -> List[Dict]:
        if not self.client:
            return []
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
            gifts = []
            if hasattr(result, 'gifts'):
                for i, gift in enumerate(result.gifts):
                    gifts.append({
                        'index': i, 'id': getattr(gift, 'id', None),
                        'title': getattr(gift, 'title', None),
                        'stars': getattr(gift, 'stars', 0),
                        'convert_stars': getattr(gift, 'convert_stars', 0),
                        'limited': getattr(gift, 'limited', False),
                        'sold_out': getattr(gift, 'sold_out', False),
                        'auction': getattr(gift, 'auction', False),
                        'birthday': getattr(gift, 'birthday', False),
                        'availability_remains': getattr(gift, 'availability_remains', None),
                        'availability_total': getattr(gift, 'availability_total', None),
                        'upgrade_stars': getattr(gift, 'upgrade_stars', None),
                        'sticker': getattr(gift, 'sticker', None),
                    })
            self.gifts_from_telegram = gifts
            print(f"вњ… РџРѕР»СѓС‡РµРЅРѕ {len(gifts)} РїРѕРґР°СЂРєРѕРІ")
            return gifts
        except Exception as e:
            print(f"вќЊ РћС€РёР±РєР°: {e}")
            return []

    def display_gifts(self, gifts: List[Dict]):
        print("\n" + "=" * 60)
        print("рџ“¦ РџРћР”РђР РљР РР— TELEGRAM")
        print("=" * 60)
        for g in gifts:
            name = g['title'] or f"ID: {g['id']}"
            flags = ""
            if g['limited']: flags += " [LIMITED]"
            if g['sold_out']: flags += " [SOLD OUT]"
            if g.get('auction'): flags += " [AUCTION]"
            if g['upgrade_stars']: flags += f" [UPG:{g['upgrade_stars']}в­ђ]"
            avail = f" ({g['availability_remains']}/{g['availability_total']})" if g['limited'] and g['availability_total'] else ""
            print(f"  {g['index'] + 1}. {name}{flags}{avail} - {g['stars']}в­ђ")
        print("-" * 60)

    def get_custom_settings(self, gift: Dict) -> Dict:
        settings = {}
        title = gift['title']
        if not title:
            title = input("Title: ").strip() or f"Gift_{gift['id']}"
        else:
            new_title = input(f"Title [{title}]: ").strip()
            if new_title: title = new_title
        settings['title'] = title
        default_stars = gift['stars']
        stars_input = input(f"Р¦РµРЅР° [{default_stars}]: ").strip()
        settings['stars'] = int(stars_input) if stars_input else default_stars
        settings['convert_stars'] = calculate_convert_stars(settings['stars'])
        print(f"   ConvertStars: {settings['convert_stars']}")
        limited_input = input("Р›РёРјРёС‚РёСЂРѕРІР°РЅРЅС‹Р№? (y/n) [n]: ").strip().lower()
        settings['limited'] = limited_input in ['y', 'yes', 'Рґ', 'РґР°']
        if settings['limited']:
            total_input = input("РљРѕР»РёС‡РµСЃС‚РІРѕ: ").strip()
            settings['availability_total'] = int(total_input) if total_input else 1000
        else:
            settings['availability_total'] = None
        return settings

    async def import_gift(self, gift: Dict, settings: Dict) -> bool:
        name = settings.get('title') or gift['title'] or f"Gift_{gift['id']}"
        print(f"\nрџ“Ґ РРјРїРѕСЂС‚: {name}")
        sticker = gift.get('sticker')
        if not sticker:
            print("   вљ  РќРµС‚ СЃС‚РёРєРµСЂР°")
            return False
        print("   рџ“¤ Р—Р°РіСЂСѓР·РєР° СЃС‚РёРєРµСЂР°...")
        sticker_data = await self.download_and_upload_document(sticker)
        if not sticker_data:
            print("   вњ— РќРµ СѓРґР°Р»РѕСЃСЊ Р·Р°РіСЂСѓР·РёС‚СЊ")
            return False
        gift_id = random.randint(1000, 1000000000)
        col = self.db[GIFTS_COLLECTION]
        while col.find_one({"GiftId": gift_id}):
            gift_id = random.randint(1000, 1000000000)
        stars = settings.get('stars', gift['stars'])
        convert_stars = settings.get('convert_stars') or calculate_convert_stars(stars)
        limited = settings.get('limited', gift['limited'])
        availability_total = settings.get('availability_total')
        # UpgradeStars is NOT set during gift import - it's only set when importing upgrade attributes
        gift_doc = {
            "GiftId": gift_id, "Limited": limited, "SoldOut": False,
            "Birthday": gift.get('birthday', False), "RequirePremium": False,
            "LimitedPerUser": False, "PeerColorAvailable": False, "Auction": gift.get('auction', False),
            "StickerId": sticker_data["DocumentId"], "Stars": stars,
            "AvailabilityRemains": availability_total if limited else None,
            "AvailabilityTotal": availability_total if limited else None,
            "AvailabilityResale": None, "ConvertStars": convert_stars,
            "FirstSaleDate": None, "LastSaleDate": None,
            "UpgradeStars": None,  # Set via import_upgrade_attributes menu
            "ResellMinStars": None, "Title": settings.get('title') or gift['title'],
            "ReleasedByPeerId": None, "ReleasedByPeerType": None,
            "PerUserTotal": None, "PerUserRemains": None, "LockedUntilDate": None,
            "UpgradeVariants": 5, "Version": 1
        }
        col.insert_one(gift_doc)
        self.db[UPGRADE_COUNTERS_COLLECTION].update_one(
            {"GiftId": gift_id},
            {"$setOnInsert": {"GiftId": gift_id, "UpgradedCount": 0, "TotalIssued": 0}},
            upsert=True
        )
        print(f"   вњ… GiftId: {gift_id}, StickerId: {sticker_data['DocumentId']}")
        return True

    # ========== РРњРџРћР Рў РђРўР РР‘РЈРўРћР’ РЈР›РЈР§РЁР•РќРР™ ==========

    async def import_upgrade_attributes(self, tg_gift_id: int, target_gift_id: int):
        """РРјРїРѕСЂС‚ РІСЃРµС… Р°С‚СЂРёР±СѓС‚РѕРІ СѓР»СѓС‡С€РµРЅРёСЏ (РјРѕРґРµР»Рё, РїР°С‚С‚РµСЂРЅС‹, С„РѕРЅС‹) РґР»СЏ РїРѕРґР°СЂРєР°."""
        print(f"\nвњЁ РРјРїРѕСЂС‚ Р°С‚СЂРёР±СѓС‚РѕРІ СѓР»СѓС‡С€РµРЅРёСЏ РґР»СЏ GiftId {target_gift_id}")
        print("   (РЎР±РѕСЂ С‡РµСЂРµР· РјРЅРѕРіРѕРєСЂР°С‚РЅС‹Р№ РІС‹Р·РѕРІ GetStarGiftUpgradePreview)")
        
        models_dict, patterns_dict, backdrops_dict = {}, {}, {}
        max_calls, no_new_streak = 300, 0
        
        for call_num in range(1, max_calls + 1):
            try:
                preview = await self.client(GetStarGiftUpgradePreviewRequest(gift_id=tg_gift_id))
                sample_attrs = getattr(preview, 'sample_attributes', [])
                found_new = False
                
                for attr in sample_attrs:
                    attr_type = type(attr).__name__
                    name = getattr(attr, 'name', None)
                    if not name:
                        continue
                    
                    if 'Model' in attr_type and name not in models_dict and name.lower() != 'original':
                        models_dict[name] = {
                            'name': name,
                            'rarity_permille': getattr(attr, 'rarity_permille', 0),
                            'sticker': getattr(attr, 'document', None)
                        }
                        found_new = True
                    elif 'Pattern' in attr_type and name not in patterns_dict:
                        patterns_dict[name] = {
                            'name': name,
                            'rarity_permille': getattr(attr, 'rarity_permille', 0),
                            'sticker': getattr(attr, 'document', None)
                        }
                        found_new = True
                    elif 'Backdrop' in attr_type and name not in backdrops_dict:
                        backdrops_dict[name] = {
                            'name': name,
                            'rarity_permille': getattr(attr, 'rarity_permille', 0),
                            'backdrop_id': getattr(attr, 'backdrop_id', call_num),
                            'center_color': getattr(attr, 'center_color', 0),
                            'edge_color': getattr(attr, 'edge_color', 0),
                            'pattern_color': getattr(attr, 'pattern_color', 0),
                            'text_color': getattr(attr, 'text_color', 0)
                        }
                        found_new = True
                
                no_new_streak = 0 if found_new else no_new_streak + 1
                if call_num % 20 == 0:
                    print(f"   [{call_num}] M:{len(models_dict)} P:{len(patterns_dict)} B:{len(backdrops_dict)}")
                if no_new_streak >= 40:
                    print(f"   вњ“ РћСЃС‚Р°РЅРѕРІРєР°: 40 РІС‹Р·РѕРІРѕРІ Р±РµР· РЅРѕРІС‹С… Р°С‚СЂРёР±СѓС‚РѕРІ")
                    break
                await asyncio.sleep(0.1)
            except Exception as e:
                print(f"   вљ  РћС€РёР±РєР° РЅР° РІС‹Р·РѕРІРµ {call_num}: {e}")
                await asyncio.sleep(1)
        
        print(f"\n   рџ“Љ РќР°Р№РґРµРЅРѕ: M:{len(models_dict)} P:{len(patterns_dict)} B:{len(backdrops_dict)}")
        
        # РРјРїРѕСЂС‚ РјРѕРґРµР»РµР№
        if models_dict:
            print(f"\nрџ“¤ РРјРїРѕСЂС‚ РјРѕРґРµР»РµР№...")
            models_col = self.db[MODELS_COLLECTION]
            for i, model in enumerate(models_dict.values(), 1):
                if not model.get('sticker'):
                    continue
                sticker_data = await self.download_and_upload_document(model['sticker'])
                if sticker_data:
                    models_col.insert_one({
                        "Name": model['name'],
                        "RarityPermille": model['rarity_permille'],
                        "GiftId": target_gift_id,
                        "DocumentId": sticker_data["DocumentId"]
                    })
                    print(f"   [{i}/{len(models_dict)}] {model['name']} вњ“")
        
        # РРјРїРѕСЂС‚ РїР°С‚С‚РµСЂРЅРѕРІ
        if patterns_dict:
            print(f"\nрџ“¤ РРјРїРѕСЂС‚ РїР°С‚С‚РµСЂРЅРѕРІ...")
            patterns_col = self.db[PATTERNS_COLLECTION]
            for i, pattern in enumerate(patterns_dict.values(), 1):
                if not pattern.get('sticker'):
                    continue
                sticker_data = await self.download_and_upload_document(pattern['sticker'])
                if sticker_data:
                    patterns_col.insert_one({
                        "Name": pattern['name'],
                        "RarityPermille": pattern['rarity_permille'],
                        "GiftId": target_gift_id,
                        "DocumentId": sticker_data["DocumentId"]
                    })
                    print(f"   [{i}/{len(patterns_dict)}] {pattern['name']} вњ“")
        
        # РРјРїРѕСЂС‚ С„РѕРЅРѕРІ
        if backdrops_dict:
            print(f"\nрџ“¤ РРјРїРѕСЂС‚ С„РѕРЅРѕРІ...")
            backdrops_col = self.db[BACKDROPS_COLLECTION]
            for i, backdrop in enumerate(backdrops_dict.values(), 1):
                backdrops_col.insert_one({
                    "Name": backdrop['name'],
                    "BackdropId": backdrop['backdrop_id'],
                    "RarityPermille": backdrop['rarity_permille'],
                    "GiftId": target_gift_id,
                    "CenterColor": backdrop['center_color'],
                    "EdgeColor": backdrop['edge_color'],
                    "PatternColor": backdrop['pattern_color'],
                    "TextColor": backdrop['text_color']
                })
                print(f"   [{i}/{len(backdrops_dict)}] {backdrop['name']} вњ“")
        
        print(f"\nвњ… РРјРїРѕСЂС‚ Р°С‚СЂРёР±СѓС‚РѕРІ Р·Р°РІРµСЂС€С‘РЅ!")
        return len(models_dict), len(patterns_dict), len(backdrops_dict)

    async def has_upgrade_attributes(self, tg_gift_id: int) -> bool:
        """Quick check whether Telegram returns any upgrade attributes for gift."""
        try:
            preview = await self.client(GetStarGiftUpgradePreviewRequest(gift_id=tg_gift_id))
            return bool(getattr(preview, 'sample_attributes', []))
        except Exception:
            return False

    @staticmethod
    def _extract_pack_short_name(value: str) -> str:
        raw = (value or "").strip()
        if not raw:
            return ""
        if raw.startswith("tg://"):
            parsed = urlparse(raw)
            set_name = parse_qs(parsed.query).get("set", [""])[0]
            return set_name.strip()

        parsed = urlparse(raw if "://" in raw else f"https://t.me/{raw.lstrip('/')}")
        parts = [p for p in (parsed.path or "").strip("/").split("/") if p]
        if not parts:
            return raw
        if parts[0] in ("addstickers", "addemoji"):
            return parts[1] if len(parts) > 1 else ""
        return parts[-1]

    async def import_pack_documents(self, is_emoji_pack: bool):
        pack_kind = "emoji-pack" if is_emoji_pack else "sticker-pack"
        prompt = "РЎСЃС‹Р»РєР°/short_name emoji pack: " if is_emoji_pack else "РЎСЃС‹Р»РєР°/short_name sticker pack: "
        short_name = self._extract_pack_short_name(input(prompt).strip())
        if not short_name:
            print("вќЊ РќРµ СѓРґР°Р»РѕСЃСЊ СЂР°СЃРїРѕР·РЅР°С‚СЊ short_name РїР°РєР°")
            return

        try:
            result = await self.client(
                GetStickerSetRequest(
                    stickerset=InputStickerSetShortName(short_name=short_name),
                    hash=0
                )
            )
        except Exception as e:
            print(f"вќЊ РћС€РёР±РєР° Р·Р°РіСЂСѓР·РєРё {pack_kind} '{short_name}': {e}")
            return

        documents = list(getattr(result, "documents", []) or [])
        set_title = getattr(getattr(result, "set", None), "title", short_name)
        print(f"\nрџ“¦ РРјРїРѕСЂС‚ {pack_kind}: {set_title} ({len(documents)} РґРѕРєСѓРјРµРЅС‚РѕРІ)")

        ok_count = 0
        for i, doc in enumerate(documents, 1):
            print(f"   [{i}/{len(documents)}] Р—Р°РіСЂСѓР·РєР°...")
            if await self.download_and_upload_document(doc):
                ok_count += 1

        print(f"вњ… Р“РѕС‚РѕРІРѕ: {ok_count}/{len(documents)} РёРјРїРѕСЂС‚РёСЂРѕРІР°РЅРѕ")

    async def run_reactions_import(self):
        script_path = base_dir / "import_reactions.py"
        if not script_path.exists():
            script_path = base_dir.parent / "import_reactions.py"
        if not script_path.exists():
            print("вќЊ РќРµ РЅР°Р№РґРµРЅ import_reactions.py")
            return

        print(f"\nвљ™пёЏ Р—Р°РїСѓСЃРє: {script_path}")
        result = subprocess.run([sys.executable, str(script_path)], cwd=str(script_path.parent))
        if result.returncode == 0:
            print("вњ… РРјРїРѕСЂС‚ СЂРµР°РєС†РёР№ Р·Р°РІРµСЂС€РµРЅ")
        else:
            print(f"вќЊ РРјРїРѕСЂС‚ СЂРµР°РєС†РёР№ Р·Р°РІРµСЂС€РёР»СЃСЏ СЃ РєРѕРґРѕРј {result.returncode}")

    async def import_upgrades_menu(self, gifts: List[Dict]):
        """РњРµРЅСЋ РёРјРїРѕСЂС‚Р° Р°С‚СЂРёР±СѓС‚РѕРІ СѓР»СѓС‡С€РµРЅРёР№."""
        print("\n" + "=" * 60)
        print("вњЁ РРњРџРћР Рў РђРўР РР‘РЈРўРћР’ РЈР›РЈР§РЁР•РќРР™")
        print("=" * 60)
        
        # Р¤РёР»СЊС‚СЂСѓРµРј СѓР»СѓС‡С€Р°РµРјС‹Рµ РїРѕРґР°СЂРєРё
        upgradeable = []
        for g in gifts:
            if g.get('upgrade_stars') or g.get('auction'):
                upgradeable.append(g)
                continue
            tg_gift_id = g.get('id')
            if tg_gift_id and await self.has_upgrade_attributes(tg_gift_id):
                upgradeable.append(g)
        if not upgradeable:
            print("вќЊ РќРµС‚ СѓР»СѓС‡С€Р°РµРјС‹С… РїРѕРґР°СЂРєРѕРІ")
            return
        
        print("\nРЈР»СѓС‡С€Р°РµРјС‹Рµ РїРѕРґР°СЂРєРё РёР· Telegram:")
        for i, g in enumerate(upgradeable, 1):
            name = g['title'] or f"ID: {g['id']}"
            upgrade_stars = g.get('upgrade_stars')
            upgrade_label = f"{upgrade_stars}в­ђ" if upgrade_stars else "n/a"
            auction_label = " [AUCTION]" if g.get('auction') else ""
            print(f"  {i}. {name}{auction_label} - {g['stars']}в­ђ (upgrade: {upgrade_label})")
        
        try:
            num = int(input("\nРќРѕРјРµСЂ РїРѕРґР°СЂРєР° РёР· Telegram: ").strip())
            if num < 1 or num > len(upgradeable):
                print("вќЊ РќРµРІРµСЂРЅС‹Р№ РЅРѕРјРµСЂ")
                return
        except ValueError:
            print("вќЊ Р’РІРµРґРёС‚Рµ С‡РёСЃР»Рѕ")
            return
        
        tg_gift = upgradeable[num - 1]
        tg_gift_id = tg_gift['id']
        print(f"\nвњ“ Р’С‹Р±СЂР°РЅ: {tg_gift['title'] or tg_gift_id}")
        
        # Р—Р°РїСЂР°С€РёРІР°РµРј GiftId РІ РЅР°С€РµР№ Р±Р°Р·Рµ
        print("\nР’РІРµРґРёС‚Рµ GiftId РїРѕРґР°СЂРєР° РІ РІР°С€РµР№ Р±Р°Р·Рµ (eventflow-stargiftreadmodel):")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("вќЊ Р’РІРµРґРёС‚Рµ С‡РёСЃР»Рѕ")
            return
        
        # РџСЂРѕРІРµСЂСЏРµРј СЃСѓС‰РµСЃС‚РІРѕРІР°РЅРёРµ
        existing = self.db[GIFTS_COLLECTION].find_one({"GiftId": target_gift_id})
        if existing:
            print(f"   вњ“ РќР°Р№РґРµРЅ: {existing.get('Title', target_gift_id)}")
        else:
            print(f"   вљ  РџРѕРґР°СЂРѕРє {target_gift_id} РЅРµ РЅР°Р№РґРµРЅ РІ Р±Р°Р·Рµ")
            if input("РџСЂРѕРґРѕР»Р¶РёС‚СЊ? (y/n): ").strip().lower() not in ['y', 'yes']:
                return
        
        # РћС‡РёС‰Р°РµРј СЃС‚Р°СЂС‹Рµ Р°С‚СЂРёР±СѓС‚С‹
        self.db[MODELS_COLLECTION].delete_many({"GiftId": target_gift_id})
        self.db[PATTERNS_COLLECTION].delete_many({"GiftId": target_gift_id})
        self.db[BACKDROPS_COLLECTION].delete_many({"GiftId": target_gift_id})
        
        # Р—Р°РїСЂР°С€РёРІР°РµРј СЃС‚РѕРёРјРѕСЃС‚СЊ СѓР»СѓС‡С€РµРЅРёСЏ
        default_upgrade_stars = tg_gift.get('upgrade_stars') or 25
        upgrade_input = input(f"РЎС‚РѕРёРјРѕСЃС‚СЊ СѓР»СѓС‡С€РµРЅРёСЏ РІ Р·РІС‘Р·РґР°С… [{default_upgrade_stars}]: ").strip()
        upgrade_stars = int(upgrade_input) if upgrade_input else default_upgrade_stars
        
        # РћР±РЅРѕРІР»СЏРµРј UpgradeStars РІ РїРѕРґР°СЂРєРµ
        self.db[GIFTS_COLLECTION].update_one(
            {"GiftId": target_gift_id},
            {"$set": {"UpgradeStars": upgrade_stars}}
        )
        print(f"   вњ“ UpgradeStars СѓСЃС‚Р°РЅРѕРІР»РµРЅ: {upgrade_stars}в­ђ")
        
        await self.import_upgrade_attributes(tg_gift_id, target_gift_id)

    async def run(self):
        """РћСЃРЅРѕРІРЅРѕР№ С†РёРєР»."""
        print("\n" + "=" * 60)
        print("рџЋЃ TELEGRAM STAR GIFTS IMPORTER")
        print("=" * 60)

        if not self.connect_mongodb():
            return
        if not self.connect_minio():
            print("вљ пёЏ  MinIO РЅРµРґРѕСЃС‚СѓРїРµРЅ")
        if not await self.connect_telegram():
            return

        gifts = await self.fetch_gifts()
        if not gifts:
            print("вќЊ РќРµС‚ РїРѕРґР°СЂРєРѕРІ")
            return

        while True:
            self.display_gifts(gifts)
            print("\n1. РРјРїРѕСЂС‚ РѕРґРЅРѕРіРѕ РїРѕРґР°СЂРєР°")
            print("2. РРјРїРѕСЂС‚ РЅРµСЃРєРѕР»СЊРєРёС… (С‡РµСЂРµР· Р·Р°РїСЏС‚СѓСЋ)")
            print("3. РРјРїРѕСЂС‚ РІСЃРµС… РїРѕРґР°СЂРєРѕРІ")
            print("4. Import upgrade attributes")
            print("5. Import reactions")
            print("6. Import emoji pack")
            print("7. Import sticker pack")
            print("0. Exit")

            choice = input("\nР’С‹Р±РѕСЂ: ").strip()

            if choice == '0':
                break
            elif choice == '1':
                num = input("РќРѕРјРµСЂ: ").strip()
                try:
                    idx = int(num) - 1
                    if 0 <= idx < len(gifts):
                        settings = self.get_custom_settings(gifts[idx])
                        await self.import_gift(gifts[idx], settings)
                except ValueError:
                    print("вќЊ РќРµРІРµСЂРЅС‹Р№ РЅРѕРјРµСЂ")
            elif choice == '2':
                nums = input("РќРѕРјРµСЂР° (1,3,5): ").strip()
                try:
                    indices = [int(n.strip()) - 1 for n in nums.split(',')]
                    for idx in indices:
                        if 0 <= idx < len(gifts):
                            settings = self.get_custom_settings(gifts[idx])
                            await self.import_gift(gifts[idx], settings)
                except ValueError:
                    print("вќЊ РќРµРІРµСЂРЅС‹Р№ С„РѕСЂРјР°С‚")
            elif choice == '3':
                confirm = input(f"РРјРїРѕСЂС‚РёСЂРѕРІР°С‚СЊ РІСЃРµ {len(gifts)}? (y/n): ").strip().lower()
                if confirm in ['y', 'yes']:
                    for gift in gifts:
                        settings = {
                            'title': gift['title'] or f"Gift_{gift['id']}",
                            'stars': gift['stars'],
                            'convert_stars': gift['convert_stars'] or calculate_convert_stars(gift['stars']),
                            'limited': gift['limited'],
                            'availability_total': gift['availability_total']
                        }
                        await self.import_gift(gift, settings)
                    print("\nвњ… Р“РѕС‚РѕРІРѕ!")
            elif choice == '4':
                await self.import_upgrades_menu(gifts)
            elif choice == '5':
                await self.run_reactions_import()
            elif choice == '6':
                await self.import_pack_documents(is_emoji_pack=True)
            elif choice == '7':
                await self.import_pack_documents(is_emoji_pack=False)

            input("\nEnter...")

    async def cleanup(self):
        if self.client:
            await self.client.disconnect()
        if self.mongo_client:
            self.mongo_client.close()


async def main():
    importer = GiftImporter()
    try:
        await importer.run()
    finally:
        await importer.cleanup()


if __name__ == '__main__':
    asyncio.run(main())

