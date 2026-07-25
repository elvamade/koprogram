#!/usr/bin/env python3
"""
Telegram Star Gifts Importer
Импортирует подарки и атрибуты улучшений из официального Telegram в локальную базу MyTelegram.

Требования:
    pip install telethon pymongo minio python-dotenv

Использование:
    1. Добавь в .env: TELEGRAM_API_ID и TELEGRAM_API_HASH
    2. Запусти: python scripts/import_gifts.py
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
    from telethon.tl.functions.payments import GetStarGiftsRequest, GetStarGiftUpgradePreviewRequest
except ImportError:
    print("❌ Telethon не установлен: pip install telethon")
    sys.exit(1)

try:
    from pymongo import MongoClient
    from bson import Binary
except ImportError:
    print("❌ PyMongo не установлен: pip install pymongo")
    sys.exit(1)

try:
    from minio import Minio
except ImportError:
    print("❌ Minio не установлен: pip install minio")
    sys.exit(1)

# ============== КОНФИГУРАЦИЯ ==============
def env_or_default(*keys: str, default: str = '') -> str:
    for key in keys:
        value = os.getenv(key)
        if value and value.strip():
            return value
    return default


API_ID = os.getenv('TELEGRAM_API_ID', '23268210')
API_HASH = os.getenv('TELEGRAM_API_HASH', '5bdfdbcfc0397f41ec13edb8720b52ea')
SESSION_NAME = 'gift_importer'

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
IMPORT_CONCURRENCY = max(1, int(os.getenv('IMPORT_CONCURRENCY', '8')))
IMPORT_REQUEST_DELAY_SECONDS = 0.0

# Коллекции
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
            print("\n⚠пёЏ  Telegram API credentials не настроены!")
            print("Добавьте в .env: TELEGRAM_API_ID и TELEGRAM_API_HASH")
            return False
        self.client = TelegramClient(SESSION_NAME, int(API_ID), API_HASH)
        self.client.session.set_dc(TG_DC_ID, TG_DC_IP, TG_DC_PORT)
        await self.client.start()
        me = await self.client.get_me()
        print(f"✅ Telegram: {me.first_name} (@{me.username})")
        return True

    def connect_mongodb(self) -> bool:
        try:
            self.mongo_client = MongoClient(MONGO_URI, serverSelectionTimeoutMS=5000)
            self.mongo_client.server_info()
            self.db = self.mongo_client[DB_NAME]
            print(f"✅ MongoDB: {MONGO_URI}")
            self._init_document_id_counter()
            self._create_indexes()
            return True
        except Exception as e:
            print(f"❌ MongoDB ошибка: {e}")
            return False

    def _init_document_id_counter(self):
        col = self.db[DOCUMENTS_COLLECTION]
        max_doc = col.find_one(sort=[("DocumentId", -1)])
        if max_doc and "DocumentId" in max_doc:
            self.document_id_counter = max_doc["DocumentId"] + 1
        else:
            self.document_id_counter = 5000000000000000000
        print(f"   📊 DocumentId: {self.document_id_counter}")

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
            print(f"✅ MinIO: {MINIO_ENDPOINT}")
            return True
        except Exception as e:
            print(f"❌ MinIO ошибка: {e}")
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
                await asyncio.to_thread(
                    self.minio_client.put_object,
                    MINIO_BUCKET,
                    str(new_doc_id),
                    BytesIO(file_data),
                    len(file_data),
                    content_type=mime_type
                )
            file_reference = hashlib.sha256(f"{new_doc_id}{time.time()}".encode()).digest()[:16]
            doc_data = {
                "DocumentId": new_doc_id, "AccessHash": new_access_hash,
                "FileReferenceBase64": base64.b64encode(file_reference).decode('ascii'),
                "Date": int(time.time()), "DcId": MY_DC_ID,
                "MimeType": mime_type, "Size": len(file_data)
            }
            await asyncio.to_thread(self._save_document_to_mongo, doc_data)
            return doc_data
        except Exception as e:
            print(f"      ✗ Ошибка: {e}")
            return None

    async def _import_documents_parallel(self, documents: List, label: str = "Документы") -> int:
        if not documents:
            return 0

        total = len(documents)
        sem = asyncio.Semaphore(IMPORT_CONCURRENCY)
        progress_lock = asyncio.Lock()
        done = 0
        ok = 0

        async def worker(doc):
            nonlocal done, ok
            async with sem:
                result = await self.download_and_upload_document(doc)
            async with progress_lock:
                done += 1
                if result:
                    ok += 1
                if done % 10 == 0 or done == total:
                    print(f"   [{label}] {done}/{total} (ok: {ok})")

        await asyncio.gather(*(worker(doc) for doc in documents))
        return ok

    async def _download_documents_parallel(self, documents: List) -> List[Optional[Dict]]:
        if not documents:
            return []
        sem = asyncio.Semaphore(IMPORT_CONCURRENCY)
        results: List[Optional[Dict]] = [None] * len(documents)

        async def worker(idx: int, doc):
            async with sem:
                results[idx] = await self.download_and_upload_document(doc)

        await asyncio.gather(*(worker(i, doc) for i, doc in enumerate(documents)))
        return results

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
            print(f"✅ Получено {len(gifts)} подарков")
            return gifts
        except Exception as e:
            print(f"❌ Ошибка: {e}")
            return []

    def display_gifts(self, gifts: List[Dict]):
        print("\n" + "=" * 60)
        print("📦 ПОДАРКИ ИЗ TELEGRAM")
        print("=" * 60)
        for g in gifts:
            name = g['title'] or f"ID: {g['id']}"
            flags = ""
            if g['limited']: flags += " [LIMITED]"
            if g['sold_out']: flags += " [SOLD OUT]"
            if g.get('auction'): flags += " [AUCTION]"
            if g['upgrade_stars']: flags += f" [UPG:{g['upgrade_stars']}⭐]"
            avail = f" ({g['availability_remains']}/{g['availability_total']})" if g['limited'] and g['availability_total'] else ""
            print(f"  {g['index'] + 1}. {name}{flags}{avail} - {g['stars']}⭐")
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
        stars_input = input(f"Цена [{default_stars}]: ").strip()
        settings['stars'] = int(stars_input) if stars_input else default_stars
        settings['convert_stars'] = calculate_convert_stars(settings['stars'])
        print(f"   ConvertStars: {settings['convert_stars']}")
        limited_input = input("Лимитированный? (y/n) [n]: ").strip().lower()
        settings['limited'] = limited_input in ['y', 'yes', 'д', 'да']
        if settings['limited']:
            total_input = input("Количество: ").strip()
            settings['availability_total'] = int(total_input) if total_input else 1000
        else:
            settings['availability_total'] = None
        return settings

    def _find_existing_gift(self, gift: Dict):
        tg_gift_id = gift.get('id')
        title = gift.get('title')
        col = self.db[GIFTS_COLLECTION]

        if tg_gift_id is not None:
            found = col.find_one({"SourceTelegramGiftId": tg_gift_id})
            if found:
                return found
        if title:
            return col.find_one({"Title": title})
        return None

    async def import_gift(self, gift: Dict, settings: Dict, existing_gift_id: Optional[int] = None) -> bool:
        name = settings.get('title') or gift['title'] or f"Gift_{gift['id']}"
        print(f"\n📥 Импорт: {name}")
        sticker = gift.get('sticker')
        if not sticker:
            print("   ⚠ Нет стикера")
            return False
        print("   📤 Загрузка стикера...")
        sticker_data = await self.download_and_upload_document(sticker)
        if not sticker_data:
            print("   ✗ Не удалось загрузить")
            return False
        gift_id = existing_gift_id if existing_gift_id else random.randint(1000, 1000000000)
        col = self.db[GIFTS_COLLECTION]
        while existing_gift_id is None and col.find_one({"GiftId": gift_id}):
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
            "SourceTelegramGiftId": gift.get('id'),
            "ReleasedByPeerId": None, "ReleasedByPeerType": None,
            "PerUserTotal": None, "PerUserRemains": None, "LockedUntilDate": None,
            "UpgradeVariants": 5, "Version": 1
        }
        if existing_gift_id is not None:
            col.replace_one({"GiftId": gift_id}, gift_doc, upsert=True)
        else:
            col.insert_one(gift_doc)
        self.db[UPGRADE_COUNTERS_COLLECTION].update_one(
            {"GiftId": gift_id},
            {"$setOnInsert": {"GiftId": gift_id, "UpgradedCount": 0, "TotalIssued": 0}},
            upsert=True
        )
        print(f"   ✅ GiftId: {gift_id}, StickerId: {sticker_data['DocumentId']}")
        return True

    # ========== ИМПОРТ АТРИБУТОВ УЛУЧШЕНИЙ ==========

    async def import_upgrade_attributes(self, tg_gift_id: int, target_gift_id: int):
        """Импорт всех атрибутов улучшения (модели, паттерны, фоны) для подарка."""
        print(f"\n✨ Импорт атрибутов улучшения для GiftId {target_gift_id}")
        print("   (Сбор через многократный вызов GetStarGiftUpgradePreview)")
        
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
                    print(f"   ✓ Остановка: 40 вызовов без новых атрибутов")
                    break
                await asyncio.sleep(IMPORT_REQUEST_DELAY_SECONDS)
            except Exception as e:
                print(f"   ⚠ Ошибка на вызове {call_num}: {e}")
                await asyncio.sleep(IMPORT_REQUEST_DELAY_SECONDS)
        
        print(f"\n   📊 Найдено: M:{len(models_dict)} P:{len(patterns_dict)} B:{len(backdrops_dict)}")
        
        # Импорт моделей
        if models_dict:
            print(f"\n📤 Импорт моделей...")
            models_col = self.db[MODELS_COLLECTION]
            model_items = [m for m in models_dict.values() if m.get('sticker')]
            model_docs: List[Optional[Dict]] = await self._download_documents_parallel(
                [m['sticker'] for m in model_items]
            )
            for i, (model, sticker_data) in enumerate(zip(model_items, model_docs), 1):
                if not sticker_data:
                    continue
                await asyncio.to_thread(
                    models_col.insert_one,
                    {
                        "Name": model['name'],
                        "RarityPermille": model['rarity_permille'],
                        "GiftId": target_gift_id,
                        "DocumentId": sticker_data["DocumentId"]
                    }
                )
                if i % 10 == 0 or i == len(model_items):
                    print(f"   [модели] {i}/{len(model_items)}")
        
        # Импорт паттернов
        if patterns_dict:
            print(f"\n📤 Импорт паттернов...")
            patterns_col = self.db[PATTERNS_COLLECTION]
            pattern_items = [p for p in patterns_dict.values() if p.get('sticker')]
            pattern_docs: List[Optional[Dict]] = await self._download_documents_parallel(
                [p['sticker'] for p in pattern_items]
            )
            for i, (pattern, sticker_data) in enumerate(zip(pattern_items, pattern_docs), 1):
                if not sticker_data:
                    continue
                await asyncio.to_thread(
                    patterns_col.insert_one,
                    {
                        "Name": pattern['name'],
                        "RarityPermille": pattern['rarity_permille'],
                        "GiftId": target_gift_id,
                        "DocumentId": sticker_data["DocumentId"]
                    }
                )
                if i % 10 == 0 or i == len(pattern_items):
                    print(f"   [паттерны] {i}/{len(pattern_items)}")
        
        # Импорт фонов
        if backdrops_dict:
            print(f"\n📤 Импорт фонов...")
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
                print(f"   [{i}/{len(backdrops_dict)}] {backdrop['name']} ✓")
        
        print(f"\n✅ Импорт атрибутов завершён!")
        return len(models_dict), len(patterns_dict), len(backdrops_dict)

    async def has_upgrade_attributes(self, tg_gift_id: int) -> bool:
        """Quick check whether Telegram returns any upgrade attributes for gift."""
        try:
            preview = await self.client(GetStarGiftUpgradePreviewRequest(gift_id=tg_gift_id))
            return bool(getattr(preview, 'sample_attributes', []))
        except Exception:
            return False

    async def _collect_upgradeable_gifts(self, gifts: List[Dict]) -> List[Dict]:
        upgradeable = []
        for g in gifts:
            if g.get('upgrade_stars') or g.get('auction'):
                upgradeable.append(g)
                continue
            tg_gift_id = g.get('id')
            if tg_gift_id and await self.has_upgrade_attributes(tg_gift_id):
                upgradeable.append(g)
        return upgradeable

    def _gift_needs_upgrade_import(self, target_gift_id: int) -> bool:
        gift = self.db[GIFTS_COLLECTION].find_one({"GiftId": target_gift_id})
        if not gift:
            return False
        if gift.get("UpgradeStars") is None:
            return True
        has_models = self.db[MODELS_COLLECTION].find_one({"GiftId": target_gift_id}) is not None
        has_patterns = self.db[PATTERNS_COLLECTION].find_one({"GiftId": target_gift_id}) is not None
        has_backdrops = self.db[BACKDROPS_COLLECTION].find_one({"GiftId": target_gift_id}) is not None
        return not (has_models and has_patterns and has_backdrops)

    async def import_all_upgrades_for_db(self, gifts: List[Dict]):
        print("\n✨ Массовый импорт атрибутов улучшений")
        upgradeable = await self._collect_upgradeable_gifts(gifts)
        if not upgradeable:
            print("❌ Нет улучшаемых подарков в Telegram")
            return

        db_gifts = list(self.db[GIFTS_COLLECTION].find({}, {"GiftId": 1, "Title": 1, "UpgradeStars": 1}))
        by_title = {}
        for item in db_gifts:
            title = (item.get("Title") or "").strip().lower()
            if title:
                by_title.setdefault(title, []).append(item)

        imported = 0
        skipped = 0
        failed = 0

        for tg in upgradeable:
            tg_title = (tg.get("title") or "").strip()
            if not tg_title:
                skipped += 1
                continue

            matches = by_title.get(tg_title.lower(), [])
            if not matches:
                skipped += 1
                continue

            for db_gift in matches:
                target_gift_id = db_gift["GiftId"]
                if not self._gift_needs_upgrade_import(target_gift_id):
                    skipped += 1
                    continue
                try:
                    self.db[MODELS_COLLECTION].delete_many({"GiftId": target_gift_id})
                    self.db[PATTERNS_COLLECTION].delete_many({"GiftId": target_gift_id})
                    self.db[BACKDROPS_COLLECTION].delete_many({"GiftId": target_gift_id})

                    upgrade_stars = tg.get('upgrade_stars') or db_gift.get("UpgradeStars") or 25
                    self.db[GIFTS_COLLECTION].update_one(
                        {"GiftId": target_gift_id},
                        {"$set": {"UpgradeStars": int(upgrade_stars)}}
                    )

                    await self.import_upgrade_attributes(tg['id'], target_gift_id)
                    imported += 1
                except Exception as e:
                    failed += 1
                    print(f"   ❌ Ошибка GiftId={target_gift_id}: {e}")

        print(f"\n✅ Массовый импорт завершён. Импортировано: {imported}, пропущено: {skipped}, ошибок: {failed}")

    async def run_reactions_import(self):
        script_path = base_dir / "import_reactions.py"
        if not script_path.exists():
            script_path = base_dir.parent / "import_reactions.py"
        if not script_path.exists():
            print("❌ Не найден import_reactions.py")
            return

        print(f"\n⚙️ Запуск: {script_path}")
        result = subprocess.run([sys.executable, str(script_path)], cwd=str(script_path.parent))
        if result.returncode == 0:
            print("✅ Импорт реакций завершен")
        else:
            print(f"❌ Импорт реакций завершился с кодом {result.returncode}")

    async def import_upgrades_menu(self, gifts: List[Dict]):
        """Меню импорта атрибутов улучшений."""
        print("\n" + "=" * 60)
        print("✨ ИМПОРТ АТРИБУТОВ УЛУЧШЕНИЙ")
        print("=" * 60)
        
        # Фильтруем улучшаемые подарки
        upgradeable = await self._collect_upgradeable_gifts(gifts)
        if not upgradeable:
            print("❌ Нет улучшаемых подарков")
            return
        
        print("\nУлучшаемые подарки из Telegram:")
        for i, g in enumerate(upgradeable, 1):
            name = g['title'] or f"ID: {g['id']}"
            upgrade_stars = g.get('upgrade_stars')
            upgrade_label = f"{upgrade_stars}⭐" if upgrade_stars else "n/a"
            auction_label = " [AUCTION]" if g.get('auction') else ""
            print(f"  {i}. {name}{auction_label} - {g['stars']}⭐ (upgrade: {upgrade_label})")
        
        try:
            num = int(input("\nНомер подарка из Telegram: ").strip())
            if num < 1 or num > len(upgradeable):
                print("❌ Неверный номер")
                return
        except ValueError:
            print("❌ Введите число")
            return
        
        tg_gift = upgradeable[num - 1]
        tg_gift_id = tg_gift['id']
        print(f"\n✓ Выбран: {tg_gift['title'] or tg_gift_id}")
        
        # Запрашиваем GiftId в нашей базе
        print("\nВведите GiftId подарка в вашей базе (eventflow-stargiftreadmodel):")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("❌ Введите число")
            return
        
        # Проверяем существование
        existing = self.db[GIFTS_COLLECTION].find_one({"GiftId": target_gift_id})
        if existing:
            print(f"   ✓ Найден: {existing.get('Title', target_gift_id)}")
        else:
            print(f"   ⚠ Подарок {target_gift_id} не найден в базе")
            if input("Продолжить? (y/n): ").strip().lower() not in ['y', 'yes']:
                return
        
        # Очищаем старые атрибуты
        self.db[MODELS_COLLECTION].delete_many({"GiftId": target_gift_id})
        self.db[PATTERNS_COLLECTION].delete_many({"GiftId": target_gift_id})
        self.db[BACKDROPS_COLLECTION].delete_many({"GiftId": target_gift_id})
        
        # Запрашиваем стоимость улучшения
        default_upgrade_stars = tg_gift.get('upgrade_stars') or 25
        upgrade_input = input(f"Стоимость улучшения в звёздах [{default_upgrade_stars}]: ").strip()
        upgrade_stars = int(upgrade_input) if upgrade_input else default_upgrade_stars
        
        # Обновляем UpgradeStars в подарке
        self.db[GIFTS_COLLECTION].update_one(
            {"GiftId": target_gift_id},
            {"$set": {"UpgradeStars": upgrade_stars}}
        )
        print(f"   ✓ UpgradeStars установлен: {upgrade_stars}⭐")
        
        await self.import_upgrade_attributes(tg_gift_id, target_gift_id)

    async def run(self):
        """Основной цикл."""
        print("\n" + "=" * 60)
        print("🎁 TELEGRAM STAR GIFTS IMPORTER")
        print("=" * 60)

        if not self.connect_mongodb():
            return
        if not self.connect_minio():
            print("⚠пёЏ  MinIO недоступен")
        if not await self.connect_telegram():
            return

        gifts = await self.fetch_gifts()
        if not gifts:
            print("❌ Нет подарков")
            return

        while True:
            self.display_gifts(gifts)
            print("\n1. Импорт одного подарка")
            print("2. Импорт нескольких (через запятую)")
            print("3. Импорт всех подарков")
            print("4. Импорт атрибутов улучшений")
            print("5. Импорт реакций")
            print("6. Импорт атрибутов для всех подарков в БД")
            print("0. Выход")

            choice = input("\nВыбор: ").strip()

            if choice == '0':
                break
            elif choice == '1':
                num = input("Номер: ").strip()
                try:
                    idx = int(num) - 1
                    if 0 <= idx < len(gifts):
                        settings = self.get_custom_settings(gifts[idx])
                        await self.import_gift(gifts[idx], settings)
                except ValueError:
                    print("❌ Неверный номер")
            elif choice == '2':
                nums = input("Номера (1,3,5): ").strip()
                try:
                    indices = [int(n.strip()) - 1 for n in nums.split(',')]
                    for idx in indices:
                        if 0 <= idx < len(gifts):
                            settings = self.get_custom_settings(gifts[idx])
                            await self.import_gift(gifts[idx], settings)
                            await asyncio.sleep(IMPORT_REQUEST_DELAY_SECONDS)
                except ValueError:
                    print("❌ Неверный формат")
            elif choice == '3':
                confirm = input(f"Импортировать все {len(gifts)}? (y/n): ").strip().lower()
                if confirm in ['y', 'yes']:
                    for gift in gifts:
                        existing = self._find_existing_gift(gift)
                        existing_gift_id = None
                        if existing:
                            existing_name = existing.get('Title') or f"GiftId {existing.get('GiftId')}"
                            overwrite = input(
                                f"Подарок уже существует ({existing_name}, GiftId={existing.get('GiftId')}). Перезаписать? (y/n): "
                            ).strip().lower()
                            if overwrite not in ['y', 'yes', 'д', 'да']:
                                print("   Пропущен")
                                await asyncio.sleep(IMPORT_REQUEST_DELAY_SECONDS)
                                continue
                            existing_gift_id = existing.get('GiftId')
                        settings = {
                            'title': gift['title'] or f"Gift_{gift['id']}",
                            'stars': gift['stars'],
                            'convert_stars': gift['convert_stars'] or calculate_convert_stars(gift['stars']),
                            'limited': gift['limited'],
                            'availability_total': gift['availability_total']
                        }
                        await self.import_gift(gift, settings, existing_gift_id=existing_gift_id)
                        await asyncio.sleep(IMPORT_REQUEST_DELAY_SECONDS)
                    print("\n✅ Готово!")
            elif choice == '4':
                await self.import_upgrades_menu(gifts)
            elif choice == '5':
                await self.run_reactions_import()
            elif choice == '6':
                await self.import_all_upgrades_for_db(gifts)

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
