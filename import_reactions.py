#!/usr/bin/env python3
"""
Скрипт для импорта реакций, стикер паков, эмодзи паков и подарков из официального Telegram на свой сервер MyTelegram.

Функции:
1. Импорт реакций - скачивает все доступные реакции из Telegram
2. Импорт стикер пака - скачивает стикер пак по ссылке и создаёт в базе
3. Импорт эмодзи пака - скачивает эмодзи пак по ссылке и создаёт в базе
4. Импорт подарков - скачивает Star Gifts из Telegram и создаёт в базе

Требования:
    pip install telethon pymongo minio

Использование:
    1. Получи API_ID и API_HASH на https://my.telegram.org
    2. Заполни конфигурацию ниже
    3. Запусти: python import_reactions.py
"""

import asyncio
import base64
import hashlib
import os
import random
import struct
import time
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

# Telethon для работы с официальным Telegram
from telethon import TelegramClient
from telethon.tl.functions.messages import GetAvailableReactionsRequest, GetStickerSetRequest
from telethon.tl.functions.payments import GetStarGiftsRequest, GetStarGiftUpgradePreviewRequest, GetResaleStarGiftsRequest
from telethon.tl.types import InputStickerSetShortName, InputStickerSetID

# MongoDB
import pymongo
from bson import Binary

# MinIO
from minio import Minio
from minio.error import S3Error

# ============================================================================
# КОНФИГУРАЦИЯ - ЗАПОЛНИ СВОИ ДАННЫЕ
# ============================================================================

# Telegram API (получить на https://my.telegram.org)
API_ID = 23268210  # Замени на свой API_ID
API_HASH = "5bdfdbcfc0397f41ec13edb8720b52ea"  # Замени на свой API_HASH
SESSION_NAME = "gift_importer"

# MongoDB твоего сервера
MONGO_URI = "mongodb://localhost:27017/"
MONGO_DATABASE = "tg"

# MinIO твоего сервера
MINIO_ENDPOINT = "127.0.0.1:9000"
MINIO_ACCESS_KEY = "test"
MINIO_SECRET_KEY = "yw2lCksTPiAS0Bgj"
MINIO_BUCKET = "tg-files"
MINIO_SECURE = False  # True если используешь HTTPS

# DC ID твоего сервера (обычно 1)
MY_DC_ID = 1

# ============================================================================


# ============================================================================
# TL SERIALIZATION HELPERS
# ============================================================================

# TL Constructor IDs
VECTOR_CONSTRUCTOR = 0x1cb5c415
DOCUMENT_ATTRIBUTE_STICKER = 0x6319d612  # TDocumentAttributeSticker
DOCUMENT_ATTRIBUTE_CUSTOM_EMOJI = 0xfd149899  # TDocumentAttributeCustomEmoji
DOCUMENT_ATTRIBUTE_IMAGE_SIZE = 0x6c37c15c  # TDocumentAttributeImageSize
DOCUMENT_ATTRIBUTE_VIDEO = 0xd38ff1c2  # TDocumentAttributeVideo
DOCUMENT_ATTRIBUTE_ANIMATED = 0x11b58939  # TDocumentAttributeAnimated
INPUT_STICKERSET_ID = 0x9de7a269  # TInputStickerSetID


def write_int(value: int) -> bytes:
    """Write 32-bit signed integer in little-endian."""
    return struct.pack('<i', value)


def write_uint(value: int) -> bytes:
    """Write 32-bit unsigned integer in little-endian."""
    return struct.pack('<I', value)


def write_long(value: int) -> bytes:
    """Write 64-bit signed integer in little-endian."""
    return struct.pack('<q', value)


def write_double(value: float) -> bytes:
    """Write 64-bit double in little-endian."""
    return struct.pack('<d', value)


def write_string(s: str) -> bytes:
    """Write TL string (length-prefixed)."""
    data = s.encode('utf-8')
    length = len(data)
    
    if length < 254:
        result = bytes([length]) + data
        # Pad to 4-byte boundary
        padding = (4 - (len(result) % 4)) % 4
        result += b'\x00' * padding
    else:
        result = bytes([254]) + struct.pack('<I', length)[:3] + data
        padding = (4 - (len(result) % 4)) % 4
        result += b'\x00' * padding
    
    return result


def serialize_input_stickerset_id(stickerset_id: int, access_hash: int) -> bytes:
    """Serialize TInputStickerSetID."""
    return write_uint(INPUT_STICKERSET_ID) + write_long(stickerset_id) + write_long(access_hash)


def serialize_document_attribute_sticker(alt: str, stickerset_id: int, stickerset_access_hash: int, mask: bool = False) -> bytes:
    """Serialize TDocumentAttributeSticker."""
    flags = 0
    if mask:
        flags |= 2  # mask flag
    
    result = write_uint(DOCUMENT_ATTRIBUTE_STICKER)
    result += write_int(flags)
    result += write_string(alt)  # emoji
    result += serialize_input_stickerset_id(stickerset_id, stickerset_access_hash)
    # mask_coords would go here if mask=True, but we skip for simplicity
    
    return result


def serialize_document_attribute_custom_emoji(alt: str, stickerset_id: int, stickerset_access_hash: int, free: bool = False, text_color: bool = False) -> bytes:
    """Serialize TDocumentAttributeCustomEmoji."""
    flags = 0
    if free:
        flags |= 1  # free flag
    if text_color:
        flags |= 2  # text_color flag
    
    result = write_uint(DOCUMENT_ATTRIBUTE_CUSTOM_EMOJI)
    result += write_int(flags)
    result += write_string(alt)  # emoji
    result += serialize_input_stickerset_id(stickerset_id, stickerset_access_hash)
    
    return result


def serialize_document_attribute_image_size(w: int, h: int) -> bytes:
    """Serialize TDocumentAttributeImageSize."""
    return write_uint(DOCUMENT_ATTRIBUTE_IMAGE_SIZE) + write_int(w) + write_int(h)


def serialize_document_attribute_video(w: int, h: int, duration: float = 0.0, round_message: bool = False, supports_streaming: bool = False) -> bytes:
    """Serialize TDocumentAttributeVideo."""
    flags = 0
    if round_message:
        flags |= 1
    if supports_streaming:
        flags |= 2
    
    result = write_uint(DOCUMENT_ATTRIBUTE_VIDEO)
    result += write_int(flags)
    result += write_double(duration)
    result += write_int(w)
    result += write_int(h)
    
    return result


def serialize_document_attribute_animated() -> bytes:
    """Serialize TDocumentAttributeAnimated."""
    return write_uint(DOCUMENT_ATTRIBUTE_ANIMATED)


def serialize_attributes_vector(attributes: list) -> bytes:
    """Serialize a vector of attributes."""
    result = write_uint(VECTOR_CONSTRUCTOR)
    result += write_int(len(attributes))
    for attr in attributes:
        result += attr
    return result


def build_sticker_attributes(
    emoji: str,
    stickerset_id: int,
    stickerset_access_hash: int,
    width: int = 512,
    height: int = 512,
    mime_type: str = "image/webp",
    is_mask: bool = False,
    is_emoji: bool = False,
    text_color: bool = False
) -> bytes:
    """Build complete Attributes field for a sticker/emoji document."""
    attributes = []
    
    # Add image size for static stickers (WebP)
    if mime_type == "image/webp":
        attributes.append(serialize_document_attribute_image_size(width, height))
    
    # Add video attribute for video stickers (WebM)
    elif mime_type == "video/webm":
        attributes.append(serialize_document_attribute_video(width, height, duration=3.0))
    
    # Add animated attribute for TGS stickers
    elif mime_type == "application/x-tgsticker":
        attributes.append(serialize_document_attribute_animated())
        attributes.append(serialize_document_attribute_image_size(width, height))
    
    # Add sticker or custom emoji attribute
    if is_emoji:
        attributes.append(serialize_document_attribute_custom_emoji(
            alt=emoji,
            stickerset_id=stickerset_id,
            stickerset_access_hash=stickerset_access_hash,
            free=True,
            text_color=text_color
        ))
    else:
        attributes.append(serialize_document_attribute_sticker(
            alt=emoji,
            stickerset_id=stickerset_id,
            stickerset_access_hash=stickerset_access_hash,
            mask=is_mask
        ))
    
    return serialize_attributes_vector(attributes)


# ============================================================================


class TelegramImporter:
    def __init__(self):
        self.client: Optional[TelegramClient] = None
        self.mongo_client: Optional[pymongo.MongoClient] = None
        self.db = None
        self.minio_client: Optional[Minio] = None
        self.document_id_counter = None
        # Temporary storage for stickerset info during import
        self._current_stickerset_id: Optional[int] = None
        self._current_stickerset_access_hash: Optional[int] = None
        self._current_is_emoji: bool = False
        self._current_text_color: bool = False
        
    async def connect(self):
        """Подключение ко всем сервисам"""
        print("=" * 60)
        print("Подключение к сервисам...")
        print("=" * 60)
        
        # Telegram
        print("\n📱 Подключение к Telegram...")
        self.client = TelegramClient(SESSION_NAME, API_ID, API_HASH)
        await self.client.start()
        me = await self.client.get_me()
        print(f"   ✓ Подключено как: {me.first_name} (@{me.username})")
        
        # MongoDB
        print("\n🗄️  Подключение к MongoDB...")
        self.mongo_client = pymongo.MongoClient(MONGO_URI)
        self.db = self.mongo_client[MONGO_DATABASE]
        # Проверка подключения
        self.mongo_client.admin.command('ping')
        print(f"   ✓ Подключено к базе: {MONGO_DATABASE}")
        
        # MinIO
        print("\n📦 Подключение к MinIO...")
        self.minio_client = Minio(
            MINIO_ENDPOINT,
            access_key=MINIO_ACCESS_KEY,
            secret_key=MINIO_SECRET_KEY,
            secure=MINIO_SECURE
        )
        # Создаём bucket если не существует
        if not self.minio_client.bucket_exists(MINIO_BUCKET):
            self.minio_client.make_bucket(MINIO_BUCKET)
            print(f"   ✓ Создан bucket: {MINIO_BUCKET}")
        else:
            print(f"   ✓ Bucket существует: {MINIO_BUCKET}")
        
        # Инициализируем счётчик DocumentId
        await self._init_document_id_counter()
        
        print("\n" + "=" * 60)
        
    async def _init_document_id_counter(self):
        """Инициализация счётчика DocumentId"""
        # Находим максимальный DocumentId в базе
        col = self.db["eventflow-documentreadmodel"]
        max_doc = col.find_one(sort=[("DocumentId", -1)])
        if max_doc and "DocumentId" in max_doc:
            self.document_id_counter = max_doc["DocumentId"] + 1
        else:
            # Начинаем с большого числа чтобы не пересекаться
            self.document_id_counter = 5000000000000000000
        print(f"   ✓ Начальный DocumentId: {self.document_id_counter}")
        
    def _get_next_document_id(self) -> int:
        """Получить следующий DocumentId"""
        doc_id = self.document_id_counter
        self.document_id_counter += 1
        return doc_id
    
    def _generate_access_hash(self) -> int:
        """Генерация случайного AccessHash"""
        return random.randint(1000000000000000000, 9223372036854775807)
    
    def _generate_sticker_set_id(self) -> int:
        """Генерация случайного StickerSetId (Int64)"""
        return random.randint(1000000000000000000, 9223372036854775807)
    
    async def download_and_upload_document(
        self, 
        document, 
        name_prefix: str,
        emoji: str = "😀",
        is_sticker: bool = False
    ) -> Optional[Dict]:
        """
        Скачивает документ из Telegram и загружает в MinIO.
        Возвращает данные для MongoDB.
        
        Args:
            document: Telegram document object or dict from to_dict()
            name_prefix: Prefix for logging
            emoji: Emoji associated with this sticker (for Attributes)
            is_sticker: If True, will generate Attributes field for sticker/emoji
        """
        if document is None:
            return None
            
        try:
            # Проверяем тип документа
            is_dict = isinstance(document, dict)
            
            # Если это dict, нужно создать InputDocument для скачивания
            if is_dict:
                from telethon.tl.types import InputDocumentFileLocation
                doc_id = document.get('id')
                access_hash = document.get('access_hash')
                file_reference = document.get('file_reference', b'')
                
                if isinstance(file_reference, str):
                    import base64 as b64
                    file_reference = b64.b64decode(file_reference)
                elif isinstance(file_reference, list):
                    file_reference = bytes(file_reference)
                
                # Скачиваем через InputDocumentFileLocation
                input_location = InputDocumentFileLocation(
                    id=doc_id,
                    access_hash=access_hash,
                    file_reference=file_reference,
                    thumb_size=''
                )
                file_data = await self.client.download_file(input_location)
                mime_type = document.get('mime_type', 'application/x-tgsticker')
            else:
                # Скачиваем файл (Telethon объект)
                file_data = await self.client.download_media(document, file=bytes)
                mime_type = getattr(document, 'mime_type', 'application/x-tgsticker')
            
            if not file_data:
                print(f"      ⚠ Не удалось скачать: {name_prefix}")
                return None
            
            # Генерируем новые ID для нашего сервера
            new_doc_id = self._get_next_document_id()
            new_access_hash = self._generate_access_hash()
            
            # Путь в MinIO - file-server ищет файлы по DocumentId напрямую
            file_path = str(new_doc_id)
            
            # Загружаем в MinIO
            from io import BytesIO
            file_stream = BytesIO(file_data)
            self.minio_client.put_object(
                MINIO_BUCKET,
                file_path,
                file_stream,
                length=len(file_data),
                content_type=mime_type
            )
            
            # Генерируем FileReference
            file_reference = hashlib.sha256(f"{new_doc_id}{time.time()}".encode()).digest()[:16]
            
            # Данные документа для MongoDB
            doc_data = {
                "DocumentId": new_doc_id,
                "AccessHash": new_access_hash,
                "FileReferenceBase64": base64.b64encode(file_reference).decode('ascii'),
                "Date": int(time.time()),
                "DcId": MY_DC_ID,
                "MimeType": mime_type,
                "Size": len(file_data)
            }
            
            # Build Attributes for stickers/emojis
            attributes_bytes = None
            if is_sticker and self._current_stickerset_id is not None:
                attributes_bytes = build_sticker_attributes(
                    emoji=emoji,
                    stickerset_id=self._current_stickerset_id,
                    stickerset_access_hash=self._current_stickerset_access_hash,
                    width=512,
                    height=512,
                    mime_type=mime_type,
                    is_mask=False,
                    is_emoji=self._current_is_emoji,
                    text_color=self._current_text_color
                )
            
            # Сохраняем в eventflow-documentreadmodel
            self._save_document_to_mongo(doc_data, attributes_bytes)
            
            return doc_data
            
        except Exception as e:
            print(f"      ✗ Ошибка при обработке {name_prefix}: {e}")
            return None
    
    def _save_document_to_mongo(self, doc_data: Dict, attributes_bytes: Optional[bytes] = None):
        """Сохраняет документ в MongoDB"""
        col = self.db["eventflow-documentreadmodel"]
        
        mongo_doc = {
            "_id": f"document-{doc_data['DocumentId']}",
            "DocumentId": doc_data["DocumentId"],
            "AccessHash": doc_data["AccessHash"],
            "FileReference": Binary(base64.b64decode(doc_data["FileReferenceBase64"])),
            "Date": doc_data["Date"],
            "DcId": doc_data["DcId"],
            "MimeType": doc_data["MimeType"],
            "Size": doc_data["Size"],
            "CreatedAt": datetime.utcnow()
        }
        
        # Add Attributes field for stickers/emojis (required for sending!)
        if attributes_bytes is not None:
            mongo_doc["Attributes"] = Binary(attributes_bytes)
        
        col.replace_one(
            {"_id": mongo_doc["_id"]},
            mongo_doc,
            upsert=True
        )

    # ========================================================================
    # ИМПОРТ РЕАКЦИЙ
    # ========================================================================
    
    async def import_reactions(self):
        """Импорт реакций из Telegram"""
        print("\n🎭 Получение списка реакций из Telegram...")
        
        # Получаем реакции из официального Telegram
        result = await self.client(GetAvailableReactionsRequest(hash=0))
        
        if not hasattr(result, 'reactions'):
            print("   ✗ Не удалось получить реакции")
            return
        
        reactions = result.reactions
        print(f"   ✓ Найдено реакций: {len(reactions)}")
        
        # Подготавливаем данные для MongoDB
        reactions_data = []
        
        for i, reaction in enumerate(reactions, 1):
            emoticon = getattr(reaction, 'reaction', '')
            title = getattr(reaction, 'title', emoticon)
            
            print(f"\n[{i}/{len(reactions)}] {emoticon} - {title}")
            
            # Скачиваем и загружаем все анимации
            sticker_data = await self.download_and_upload_document(
                getattr(reaction, 'static_icon', None),
                f"{emoticon}_static"
            )
            
            appear_data = await self.download_and_upload_document(
                getattr(reaction, 'appear_animation', None),
                f"{emoticon}_appear"
            )
            
            select_data = await self.download_and_upload_document(
                getattr(reaction, 'select_animation', None),
                f"{emoticon}_select"
            )
            
            activate_data = await self.download_and_upload_document(
                getattr(reaction, 'activate_animation', None),
                f"{emoticon}_activate"
            )
            
            effect_data = await self.download_and_upload_document(
                getattr(reaction, 'effect_animation', None),
                f"{emoticon}_effect"
            )
            
            around_data = await self.download_and_upload_document(
                getattr(reaction, 'around_animation', None),
                f"{emoticon}_around"
            )
            
            center_data = await self.download_and_upload_document(
                getattr(reaction, 'center_icon', None),
                f"{emoticon}_center"
            )
            
            # Формируем данные реакции
            reaction_doc = {
                "type": "emoji",
                "emoticon": emoticon,
                "title": title,
                "inactive": getattr(reaction, 'inactive', False),
                "premium": getattr(reaction, 'premium', False)
            }
            
            # Добавляем данные стикеров если есть
            if sticker_data:
                reaction_doc["sticker"] = sticker_data
            if appear_data:
                reaction_doc["appearAnimation"] = appear_data
            if select_data:
                reaction_doc["selectAnimation"] = select_data
            if activate_data:
                reaction_doc["activateAnimation"] = activate_data
            if effect_data:
                reaction_doc["effectAnimation"] = effect_data
            if around_data:
                reaction_doc["aroundAnimation"] = around_data
            if center_data:
                reaction_doc["centerIcon"] = center_data
            
            reactions_data.append(reaction_doc)
            print(f"   ✓ Обработано: {emoticon}")
        
        # Сохраняем в available_reactions
        print("\n\n💾 Сохранение в MongoDB...")
        self._save_reactions_to_mongo(reactions_data)
        
        print("\n" + "=" * 60)
        print(f"✅ Импорт завершён! Добавлено реакций: {len(reactions_data)}")
        print("=" * 60)
    
    def _save_reactions_to_mongo(self, reactions_data: List[Dict]):
        """Сохраняет реакции в коллекцию available_reactions"""
        col = self.db["available_reactions"]
        
        # Удаляем старые данные
        col.delete_many({})
        
        # Вставляем новый документ
        doc = {
            "_id": "global",
            "reactions": reactions_data,
            "importedAt": datetime.utcnow(),
            "count": len(reactions_data)
        }
        
        col.insert_one(doc)
        print(f"   ✓ Сохранено {len(reactions_data)} реакций")

    # ========================================================================
    # ИМПОРТ СТИКЕР/ЭМОДЗИ ПАКОВ
    # ========================================================================
    
    def _check_stickerset_exists(self, short_name: str) -> bool:
        """Проверяет существует ли стикер пак с таким ShortName"""
        col = self.db["stickersets"]
        return col.find_one({"ShortName": short_name}) is not None
    
    def _extract_stickerset_shortname(self, url: str) -> Optional[str]:
        """Извлекает ShortName из ссылки на стикер пак"""
        # Поддерживаемые форматы:
        # https://t.me/addstickers/PackName
        # https://t.me/addemoji/PackName
        # t.me/addstickers/PackName
        # PackName (просто имя)
        
        url = url.strip()
        
        if "t.me/addstickers/" in url:
            return url.split("t.me/addstickers/")[-1].split("?")[0].split("/")[0]
        elif "t.me/addemoji/" in url:
            return url.split("t.me/addemoji/")[-1].split("?")[0].split("/")[0]
        elif "/" not in url and "." not in url:
            # Просто имя пака
            return url
        
        return None
    
    async def import_sticker_pack(self, is_emoji: bool = False):
        """Импорт стикер пака или эмодзи пака"""
        pack_type = "эмодзи" if is_emoji else "стикер"
        
        print(f"\n{'🎨' if is_emoji else '🖼️'} Импорт {pack_type} пака")
        print("=" * 60)
        
        # Запрашиваем ссылку на пак
        print(f"\nВведите ссылку на {pack_type} пак из Telegram")
        print("(например: https://t.me/addstickers/PackName или просто PackName)")
        url = input("Ссылка: ").strip()
        
        # Извлекаем ShortName из ссылки
        source_shortname = self._extract_stickerset_shortname(url)
        if not source_shortname:
            print("   ✗ Не удалось извлечь имя пака из ссылки")
            return
        
        print(f"   ✓ Имя пака в Telegram: {source_shortname}")
        
        # Получаем стикер пак из Telegram
        print(f"\n📥 Загрузка {pack_type} пака из Telegram...")
        try:
            sticker_set = await self.client(GetStickerSetRequest(
                stickerset=InputStickerSetShortName(short_name=source_shortname),
                hash=0
            ))
        except Exception as e:
            print(f"   ✗ Ошибка при получении пака: {e}")
            return
        
        if not sticker_set or not sticker_set.set:
            print("   ✗ Стикер пак не найден")
            return
        
        original_set = sticker_set.set
        documents = sticker_set.documents
        packs = sticker_set.packs  # Связь стикеров с эмодзи
        
        print(f"   ✓ Найден пак: {original_set.title}")
        print(f"   ✓ Стикеров: {len(documents)}")
        
        # Запрашиваем ShortName для нашего сервера
        print(f"\nВведите ShortName для {pack_type} пака на вашем сервере")
        print("(латинские буквы, цифры и подчёркивания)")
        new_shortname = input("ShortName: ").strip()
        
        if not new_shortname:
            print("   ✗ ShortName не может быть пустым")
            return
        
        # Проверяем что такого ShortName ещё нет
        if self._check_stickerset_exists(new_shortname):
            print(f"   ✗ Стикер пак с ShortName '{new_shortname}' уже существует!")
            return
        
        print(f"   ✓ ShortName '{new_shortname}' свободен")
        
        # Запрашиваем Title
        print(f"\nВведите Title (название) для {pack_type} пака")
        print(f"(по умолчанию: {original_set.title})")
        new_title = input("Title: ").strip()
        if not new_title:
            new_title = original_set.title
        
        print(f"   ✓ Title: {new_title}")
        
        # Генерируем StickerSetId и AccessHash
        sticker_set_id = self._generate_sticker_set_id()
        access_hash = self._generate_access_hash()
        
        print(f"\n   ✓ StickerSetId: {sticker_set_id}")
        print(f"   ✓ AccessHash: {access_hash}")
        
        # Store stickerset info for Attributes generation
        self._current_stickerset_id = sticker_set_id
        self._current_stickerset_access_hash = access_hash
        self._current_is_emoji = is_emoji
        self._current_text_color = getattr(original_set, 'text_color', False)
        
        # Создаём маппинг document_id -> emoji из packs
        doc_to_emoji = {}
        for pack in packs:
            emoji = pack.emoticon
            for doc_id in pack.documents:
                doc_to_emoji[doc_id] = emoji
        
        # Скачиваем и загружаем все стикеры
        print(f"\n📤 Загрузка стикеров на сервер...")
        stickers_data = []
        
        for i, doc in enumerate(documents, 1):
            emoji = doc_to_emoji.get(doc.id, "😀")  # Дефолтный эмодзи если не найден
            print(f"\n[{i}/{len(documents)}] Стикер {doc.id} ({emoji})")
            
            # Скачиваем и загружаем с Attributes для стикеров
            sticker_data = await self.download_and_upload_document(
                doc, 
                f"sticker_{i}",
                emoji=emoji,
                is_sticker=True  # This will generate Attributes field
            )
            
            if sticker_data:
                # Добавляем эмодзи к данным стикера
                sticker_data["Emoji"] = emoji
                stickers_data.append(sticker_data)
                print(f"   ✓ Загружен (с Attributes)")
            else:
                print(f"   ⚠ Пропущен")
        
        # Clear temporary stickerset info
        self._current_stickerset_id = None
        self._current_stickerset_access_hash = None
        self._current_is_emoji = False
        self._current_text_color = False
        
        # Сохраняем стикер пак в MongoDB
        print(f"\n💾 Сохранение {pack_type} пака в MongoDB...")
        
        stickerset_doc = {
            "StickerSetId": sticker_set_id,
            "AccessHash": access_hash,
            "Title": new_title,
            "ShortName": new_shortname,
            "Masks": False,
            "Emojis": is_emoji,  # True для эмодзи паков, False для стикер паков
            "TextColor": False,
            "Date": int(time.time()),
            "Stickers": stickers_data
        }
        
        col = self.db["stickersets"]
        col.insert_one(stickerset_doc)
        
        print("\n" + "=" * 60)
        print(f"✅ {pack_type.capitalize()} пак успешно импортирован!")
        print(f"   ShortName: {new_shortname}")
        print(f"   Title: {new_title}")
        print(f"   Стикеров: {len(stickers_data)}")
        print(f"   StickerSetId: {sticker_set_id}")
        print("=" * 60)

    # ========================================================================
    # ИМПОРТ ПОДАРКОВ (STAR GIFTS)
    # ========================================================================
    
    async def import_star_gifts(self):
        """Импорт Star Gifts из Telegram"""
        print("\n🎁 Импорт подарков (Star Gifts)")
        print("=" * 60)
        
        # Получаем все подарки из Telegram
        print("\n📥 Загрузка списка подарков из Telegram...")
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
        except Exception as e:
            print(f"   ✗ Ошибка при получении подарков: {e}")
            return
        
        if not hasattr(result, 'gifts') or not result.gifts:
            print("   ✗ Подарки не найдены")
            return
        
        gifts = result.gifts
        print(f"   ✓ Найдено подарков: {len(gifts)}")
        
        # Собираем информацию о подарках
        gifts_info = []
        for i, gift in enumerate(gifts):
            gift_id = getattr(gift, 'id', None)
            title = getattr(gift, 'title', None)
            stars = getattr(gift, 'stars', 0)
            convert_stars = getattr(gift, 'convert_stars', 0)
            limited = getattr(gift, 'limited', False)
            availability_remains = getattr(gift, 'availability_remains', None)
            availability_total = getattr(gift, 'availability_total', None)
            sticker = getattr(gift, 'sticker', None)
            
            gifts_info.append({
                'index': i,
                'id': gift_id,
                'title': title,
                'stars': stars,
                'convert_stars': convert_stars,
                'limited': limited,
                'availability_remains': availability_remains,
                'availability_total': availability_total,
                'sticker': sticker,
                'original': gift
            })
        
        # Показываем список подарков
        print("\n📋 Список доступных подарков:")
        print("-" * 60)
        for g in gifts_info:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            limited_str = " [LIMITED]" if g['limited'] else ""
            avail_str = ""
            if g['limited'] and g['availability_total']:
                avail_str = f" ({g['availability_remains']}/{g['availability_total']})"
            print(f"  {g['index'] + 1}. {display_name}{limited_str}{avail_str} - {g['stars']}⭐ (конверт: {g['convert_stars']}⭐)")
        print("-" * 60)
        
        # Выбор режима импорта
        print("\nВыберите режим импорта:")
        print("  1. Импортировать один подарок")
        print("  2. Импортировать несколько подарков")
        print("  3. Импортировать все подарки")
        print("  9. [DEBUG] Показать все поля подарка")
        print("  0. Назад")
        
        choice = input("\nВаш выбор: ").strip()
        
        if choice == "0":
            return
        elif choice == "1":
            await self._import_single_gift(gifts_info)
        elif choice == "2":
            await self._import_multiple_gifts(gifts_info)
        elif choice == "3":
            await self._import_all_gifts(gifts_info)
        elif choice == "9":
            await self._debug_gift_fields(gifts_info)
        else:
            print("   ⚠ Неверный выбор")
    
    async def _debug_gift_fields(self, gifts_info: List[Dict]):
        """Показать все поля подарка для дебага"""
        print("\n🔍 DEBUG: Просмотр полей подарка")
        print("-" * 60)
        
        print("\nВведите номер подарка для просмотра:")
        for g in gifts_info:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name}")
        
        try:
            num = int(input("\nНомер: ").strip())
            if num < 1 or num > len(gifts_info):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        gift = gifts_info[num - 1]['original']
        
        print(f"\n{'='*60}")
        print(f"Тип объекта: {type(gift).__name__}")
        print(f"{'='*60}")
        
        # Проверяем sticker и его атрибуты - ищем stickerset!
        sticker = getattr(gift, 'sticker', None)
        if sticker:
            print("\n🎯 STICKER ATTRIBUTES (ищем stickerset):")
            sticker_attrs = getattr(sticker, 'attributes', [])
            for i, attr in enumerate(sticker_attrs):
                attr_type = type(attr).__name__
                print(f"\n  [{i}] {attr_type}:")
                
                # Показываем все поля атрибута
                for field in dir(attr):
                    if field.startswith('_'):
                        continue
                    try:
                        val = getattr(attr, field)
                        if callable(val):
                            continue
                        
                        # Особое внимание на stickerset!
                        if field == 'stickerset':
                            print(f"      🎯🎯🎯 STICKERSET FOUND! 🎯🎯🎯")
                            print(f"      .{field} = {val}")
                            if val:
                                for ss_field in dir(val):
                                    if ss_field.startswith('_'):
                                        continue
                                    try:
                                        ss_val = getattr(val, ss_field)
                                        if not callable(ss_val):
                                            print(f"          .{ss_field} = {ss_val}")
                                    except:
                                        pass
                        else:
                            print(f"      .{field} = {val}")
                    except:
                        pass
        
        print(f"\n{'='*60}")
        print("\nОстальные поля подарка:")
        for attr in ['id', 'title', 'stars', 'upgrade_stars', 'limited', 'birthday']:
            try:
                val = getattr(gift, attr, None)
                print(f"  {attr} = {val}")
            except:
                pass
        
        print(f"\n{'='*60}")
        input("\nНажмите Enter для продолжения...")

    async def _import_single_gift(self, gifts_info: List[Dict]):
        """Импорт одного подарка с ручным вводом параметров"""
        print("\n🎁 Импорт одного подарка")
        print("-" * 60)
        
        # Показываем список для выбора
        print("\nВведите номер подарка для импорта:")
        for g in gifts_info:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name}")
        
        try:
            num = int(input("\nНомер: ").strip())
            if num < 1 or num > len(gifts_info):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        gift = gifts_info[num - 1]
        await self._import_gift_with_manual_params(gift)
    
    async def _import_multiple_gifts(self, gifts_info: List[Dict]):
        """Импорт нескольких подарков с ручным вводом параметров"""
        print("\n🎁 Импорт нескольких подарков")
        print("-" * 60)
        
        # Показываем список для выбора
        print("\nВведите номера подарков через запятую (например: 1,3,5):")
        for g in gifts_info:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name}")
        
        nums_str = input("\nНомера: ").strip()
        try:
            nums = [int(n.strip()) for n in nums_str.split(",")]
        except ValueError:
            print("   ✗ Неверный формат. Введите числа через запятую")
            return
        
        for num in nums:
            if num < 1 or num > len(gifts_info):
                print(f"   ⚠ Пропущен неверный номер: {num}")
                continue
            
            gift = gifts_info[num - 1]
            print(f"\n{'=' * 40}")
            await self._import_gift_with_manual_params(gift)
    
    async def _import_gift_with_manual_params(self, gift: Dict):
        """Импорт подарка с ручным вводом параметров"""
        display_name = gift['title'] if gift['title'] else f"ID: {gift['id']}"
        print(f"\n📦 Импорт подарка: {display_name}")
        
        # Запрашиваем Title если его нет
        title = gift['title']
        if not title:
            title = input("Введите название подарка (Title): ").strip()
            if not title:
                print("   ✗ Title не может быть пустым")
                return
        else:
            new_title = input(f"Title [{title}]: ").strip()
            if new_title:
                title = new_title
        
        # Запрашиваем лимитированность
        limited_input = input("Лимитированный подарок? (y/n) [n]: ").strip().lower()
        limited = limited_input == 'y' or limited_input == 'yes' or limited_input == 'д' or limited_input == 'да'
        
        # Если лимитированный - запрашиваем количество
        availability_total = 0
        if limited:
            try:
                availability_total = int(input("Общее количество подарков: ").strip())
            except ValueError:
                print("   ✗ Введите число")
                return
        
        # Запрашиваем цену
        try:
            stars = int(input(f"Цена в звёздах [{gift['stars']}]: ").strip() or gift['stars'])
        except ValueError:
            print("   ✗ Введите число")
            return
        
        # Вычисляем цену конвертации (85% от цены)
        convert_stars = int(stars * 0.85)
        print(f"   ✓ Цена конвертации (85%): {convert_stars}⭐")
        
        # Импортируем подарок
        await self._save_gift_to_db(
            gift=gift,
            title=title,
            stars=stars,
            convert_stars=convert_stars,
            limited=limited,
            availability_total=availability_total
        )
    
    async def _import_all_gifts(self, gifts_info: List[Dict]):
        """Импорт всех подарков с данными из Telegram"""
        print("\n🎁 Импорт всех подарков")
        print("-" * 60)
        
        confirm = input(f"Импортировать все {len(gifts_info)} подарков? (y/n): ").strip().lower()
        if confirm not in ['y', 'yes', 'д', 'да']:
            print("   Отменено")
            return
        
        success_count = 0
        for i, gift in enumerate(gifts_info, 1):
            display_name = gift['title'] if gift['title'] else f"ID: {gift['id']}"
            print(f"\n[{i}/{len(gifts_info)}] {display_name}")
            
            # Используем данные из Telegram
            title = gift['title'] or f"Gift_{gift['id']}"
            stars = gift['stars']
            convert_stars = gift['convert_stars'] or int(stars * 0.85)
            limited = gift['limited']
            availability_total = gift['availability_total'] or 0
            
            try:
                await self._save_gift_to_db(
                    gift=gift,
                    title=title,
                    stars=stars,
                    convert_stars=convert_stars,
                    limited=limited,
                    availability_total=availability_total
                )
                success_count += 1
            except Exception as e:
                print(f"   ✗ Ошибка: {e}")
        
        print("\n" + "=" * 60)
        print(f"✅ Импортировано подарков: {success_count}/{len(gifts_info)}")
        print("=" * 60)
    
    async def _save_gift_to_db(
        self,
        gift: Dict,
        title: str,
        stars: int,
        convert_stars: int,
        limited: bool,
        availability_total: int
    ):
        """Сохраняет подарок в MongoDB"""
        # Скачиваем и загружаем стикер
        sticker = gift.get('sticker')
        if not sticker:
            print("   ⚠ У подарка нет стикера, пропускаем")
            return
        
        print("   📤 Загрузка стикера...")
        sticker_data = await self.download_and_upload_document(
            sticker,
            f"gift_{gift['id']}_sticker"
        )
        
        if not sticker_data:
            print("   ✗ Не удалось загрузить стикер")
            return
        
        # Генерируем ID для подарка
        gift_id = random.randint(1000, 1000000000)
        
        # Проверяем что такого ID ещё нет
        col = self.db["stargifts"]
        while col.find_one({"Id": gift_id}):
            gift_id = random.randint(1000, 1000000000)
        
        # Формируем документ подарка
        gift_doc = {
            "Id": gift_id,
            "Title": title,
            "Stars": stars,
            "ConvertStars": convert_stars,
            "Sticker": {
                "DocumentId": sticker_data["DocumentId"],
                "AccessHash": sticker_data["AccessHash"],
                "FileReferenceBase64": sticker_data["FileReferenceBase64"],
                "Date": sticker_data["Date"],
                "DcId": sticker_data["DcId"],
                "MimeType": sticker_data["MimeType"],
                "Size": sticker_data["Size"]
            },
            "Limited": limited,
            "SoldOut": False,
            "Birthday": False
        }
        
        # Добавляем поля для лимитированных подарков
        if limited:
            gift_doc["AvailabilityRemains"] = availability_total
            gift_doc["AvailabilityTotal"] = availability_total
        
        # Добавляем null поля для дат
        gift_doc["FirstSaleDate"] = None
        gift_doc["LastSaleDate"] = None
        
        # Сохраняем в MongoDB
        col.insert_one(gift_doc)
        
        print(f"   ✓ Подарок сохранён: {title} (ID: {gift_id})")
        print(f"     Цена: {stars}⭐, Конверт: {convert_stars}⭐, Limited: {limited}")

    # ========================================================================
    # ИМПОРТ МОДЕЛЕЙ УЛУЧШЕНИЙ ПОДАРКОВ
    # ========================================================================
    
    async def import_gift_upgrade_models(self):
        """Импорт моделей улучшений подарков из Telegram через GetStarGiftUpgradePreview"""
        print("\n✨ Импорт моделей улучшений подарков")
        print("=" * 60)
        
        # Получаем все подарки из Telegram
        print("\n📥 Загрузка списка подарков из Telegram...")
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
        except Exception as e:
            print(f"   ✗ Ошибка при получении подарков: {e}")
            return
        
        if not hasattr(result, 'gifts') or not result.gifts:
            print("   ✗ Подарки не найдены")
            return
        
        gifts = result.gifts
        
        # Фильтруем только улучшаемые подарки (у которых есть upgrade_stars)
        upgradeable_gifts = []
        for i, gift in enumerate(gifts):
            upgrade_stars = getattr(gift, 'upgrade_stars', None)
            if upgrade_stars and upgrade_stars > 0:
                gift_id = getattr(gift, 'id', None)
                title = getattr(gift, 'title', None)
                stars = getattr(gift, 'stars', 0)
                upgradeable_gifts.append({
                    'index': len(upgradeable_gifts),
                    'id': gift_id,
                    'title': title,
                    'stars': stars,
                    'upgrade_stars': upgrade_stars,
                    'original': gift
                })
        
        if not upgradeable_gifts:
            print("   ✗ Улучшаемые подарки не найдены")
            return
        
        print(f"   ✓ Найдено улучшаемых подарков: {len(upgradeable_gifts)}")
        
        # Показываем список улучшаемых подарков
        print("\n📋 Список улучшаемых подарков:")
        print("-" * 60)
        for g in upgradeable_gifts:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name} - {g['stars']}⭐ (апгрейд: {g['upgrade_stars']}⭐)")
        print("-" * 60)
        
        # Выбор подарка для получения моделей
        print("\nВведите номер подарка для получения ВСЕХ его моделей улучшения:")
        try:
            num = int(input("Номер: ").strip())
            if num < 1 or num > len(upgradeable_gifts):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        selected_gift = upgradeable_gifts[num - 1]
        tg_gift_id = selected_gift['id']
        display_name = selected_gift['title'] if selected_gift['title'] else f"ID: {tg_gift_id}"
        print(f"\n   ✓ Выбран подарок: {display_name}")
        
        # Используем GetStarGiftUpgradePreview для получения атрибутов
        print("\n📥 Загрузка моделей через GetStarGiftUpgradePreview...")
        try:
            preview_result = await self.client(GetStarGiftUpgradePreviewRequest(
                gift_id=tg_gift_id
            ))
        except Exception as e:
            print(f"   ✗ Ошибка при получении атрибутов: {e}")
            return
        
        if not preview_result:
            print("   ✗ Результат не получен")
            return
        
        sample_attrs = getattr(preview_result, 'sample_attributes', [])
        print(f"   ✓ Получено sample_attributes: {len(sample_attrs)}")
        
        # Пробуем найти стикерпак моделей
        models = []
        models_stickerset = None
        
        for attr in sample_attrs:
            attr_type = type(attr).__name__
            if 'Model' in attr_type or 'StarGiftAttributeModel' in attr_type:
                doc = getattr(attr, 'document', None)
                if doc:
                    for doc_attr in getattr(doc, 'attributes', []):
                        stickerset = getattr(doc_attr, 'stickerset', None)
                        if stickerset:
                            models_stickerset = stickerset
                            break
        
        # Загружаем полный стикерпак если нашли
        if models_stickerset:
            print("\n📥 Загрузка полного стикерпака моделей...")
            try:
                stickerset_id = getattr(models_stickerset, 'id', None)
                stickerset_access_hash = getattr(models_stickerset, 'access_hash', None)
                if stickerset_id and stickerset_access_hash:
                    models_set = await self.client(GetStickerSetRequest(
                        stickerset=InputStickerSetID(id=stickerset_id, access_hash=stickerset_access_hash),
                        hash=0
                    ))
                    if models_set and models_set.documents:
                        print(f"   ✓ Найдено моделей в стикерпаке: {len(models_set.documents)}")
                        doc_to_emoji = {}
                        for pack in getattr(models_set, 'packs', []):
                            emoji = pack.emoticon
                            for doc_id in pack.documents:
                                doc_to_emoji[doc_id] = emoji
                        
                        for doc in models_set.documents:
                            model_name = doc_to_emoji.get(doc.id, f"Model_{doc.id}")
                            if model_name.lower() != 'original':
                                models.append({
                                    'name': model_name,
                                    'rarity_permille': 0,
                                    'sticker': doc
                                })
            except Exception as e:
                print(f"   ⚠ Не удалось загрузить стикерпак: {e}")
        
        # Если стикерпак не найден, используем sample_attributes
        if not models:
            print("   ℹ Стикерпак не найден, используем sample_attributes...")
            for attr in sample_attrs:
                attr_type = type(attr).__name__
                if 'Model' in attr_type or 'StarGiftAttributeModel' in attr_type:
                    model_name = getattr(attr, 'name', None)
                    if model_name and model_name.lower() != 'original':
                        models.append({
                            'name': model_name,
                            'rarity_permille': getattr(attr, 'rarity_permille', 0),
                            'sticker': getattr(attr, 'document', None)
                        })
        
        if not models:
            print("   ✗ Модели улучшения не найдены")
            return
        
        print(f"   ✓ Найдено моделей: {len(models)}")
        
        # Показываем список моделей
        print("\n📋 Список моделей улучшения:")
        print("-" * 60)
        for i, m in enumerate(models, 1):
            print(f"  {i}. {m['name']} (редкость: {m['rarity_permille']}‰)")
        print("-" * 60)
        
        # Запрашиваем GiftId для которого применить модели
        print("\nВведите GiftId для которого применить эти модели:")
        print("(это ID подарка в вашей базе stargifts)")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("   ✗ Введите число")
            return
        
        # Проверяем что такой подарок существует
        col = self.db["stargifts"]
        existing_gift = col.find_one({"Id": target_gift_id})
        if not existing_gift:
            print(f"   ⚠ Подарок с ID {target_gift_id} не найден в базе")
            confirm = input("Продолжить всё равно? (y/n): ").strip().lower()
            if confirm not in ['y', 'yes', 'д', 'да']:
                print("   Отменено")
                return
        else:
            gift_title = existing_gift.get('Title', f"ID: {target_gift_id}")
            print(f"   ✓ Найден подарок: {gift_title}")
        
        # Импортируем модели
        print(f"\n📤 Импорт моделей для GiftId {target_gift_id}...")
        models_col = self.db["stargift_models"]
        success_count = 0
        
        for i, model in enumerate(models, 1):
            print(f"\n[{i}/{len(models)}] {model['name']}")
            
            if not model.get('sticker'):
                print("   ⚠ Нет стикера, пропускаем")
                continue
            
            print("   📤 Загрузка стикера...")
            sticker_data = await self.download_and_upload_document(
                model['sticker'],
                f"model_{model['name']}"
            )
            
            if not sticker_data:
                print("   ✗ Не удалось загрузить стикер")
                continue
            
            model_doc = {
                "name": model['name'],
                "rarityPermille": model['rarity_permille'],
                "GiftId": target_gift_id,
                "Sticker": {
                    "DocumentId": sticker_data["DocumentId"],
                    "AccessHash": sticker_data["AccessHash"],
                    "FileReferenceBase64": sticker_data["FileReferenceBase64"],
                    "Date": sticker_data["Date"],
                    "DcId": sticker_data["DcId"],
                    "MimeType": sticker_data["MimeType"],
                    "Size": sticker_data["Size"]
                }
            }
            
            models_col.insert_one(model_doc)
            success_count += 1
            print(f"   ✓ Модель сохранена: {model['name']}")
        
        print("\n" + "=" * 60)
        print(f"✅ Импортировано моделей: {success_count}/{len(models)}")
        print(f"   GiftId: {target_gift_id}")
        print("=" * 60)

    # ========================================================================
    # ИМПОРТ ПАТТЕРНОВ УЛУЧШЕНИЙ ПОДАРКОВ
    # ========================================================================
    
    async def import_gift_upgrade_patterns(self):
        """Импорт паттернов улучшений подарков из Telegram через GetStarGiftUpgradePreview"""
        print("\n🎨 Импорт паттернов улучшений подарков")
        print("=" * 60)
        
        # Получаем все подарки из Telegram
        print("\n📥 Загрузка списка подарков из Telegram...")
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
        except Exception as e:
            print(f"   ✗ Ошибка при получении подарков: {e}")
            return
        
        if not hasattr(result, 'gifts') or not result.gifts:
            print("   ✗ Подарки не найдены")
            return
        
        gifts = result.gifts
        
        # Фильтруем только улучшаемые подарки
        upgradeable_gifts = []
        for gift in gifts:
            upgrade_stars = getattr(gift, 'upgrade_stars', None)
            if upgrade_stars and upgrade_stars > 0:
                gift_id = getattr(gift, 'id', None)
                title = getattr(gift, 'title', None)
                stars = getattr(gift, 'stars', 0)
                upgradeable_gifts.append({
                    'index': len(upgradeable_gifts),
                    'id': gift_id,
                    'title': title,
                    'stars': stars,
                    'upgrade_stars': upgrade_stars,
                    'original': gift
                })
        
        if not upgradeable_gifts:
            print("   ✗ Улучшаемые подарки не найдены")
            return
        
        print(f"   ✓ Найдено улучшаемых подарков: {len(upgradeable_gifts)}")
        
        # Показываем список
        print("\n📋 Список улучшаемых подарков:")
        print("-" * 60)
        for g in upgradeable_gifts:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name} - {g['stars']}⭐")
        print("-" * 60)
        
        # Выбор подарка
        print("\nВведите номер подарка для получения ВСЕХ его паттернов:")
        try:
            num = int(input("Номер: ").strip())
            if num < 1 or num > len(upgradeable_gifts):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        selected_gift = upgradeable_gifts[num - 1]
        tg_gift_id = selected_gift['id']
        display_name = selected_gift['title'] if selected_gift['title'] else f"ID: {tg_gift_id}"
        print(f"\n   ✓ Выбран подарок: {display_name}")
        
        # Используем GetStarGiftUpgradePreview для получения атрибутов
        print("\n📥 Загрузка паттернов через GetStarGiftUpgradePreview...")
        try:
            preview_result = await self.client(GetStarGiftUpgradePreviewRequest(
                gift_id=tg_gift_id
            ))
        except Exception as e:
            print(f"   ✗ Ошибка при получении атрибутов: {e}")
            return
        
        if not preview_result:
            print("   ✗ Результат не получен")
            return
        
        sample_attrs = getattr(preview_result, 'sample_attributes', [])
        print(f"   ✓ Получено sample_attributes: {len(sample_attrs)}")
        
        # Пробуем найти стикерпак паттернов
        patterns = []
        patterns_stickerset = None
        
        for attr in sample_attrs:
            attr_type = type(attr).__name__
            if 'Pattern' in attr_type or 'StarGiftAttributePattern' in attr_type:
                doc = getattr(attr, 'document', None)
                if doc:
                    for doc_attr in getattr(doc, 'attributes', []):
                        stickerset = getattr(doc_attr, 'stickerset', None)
                        if stickerset:
                            patterns_stickerset = stickerset
                            break
        
        # Загружаем полный стикерпак если нашли
        if patterns_stickerset:
            print("\n📥 Загрузка полного стикерпака паттернов...")
            try:
                stickerset_id = getattr(patterns_stickerset, 'id', None)
                stickerset_access_hash = getattr(patterns_stickerset, 'access_hash', None)
                if stickerset_id and stickerset_access_hash:
                    patterns_set = await self.client(GetStickerSetRequest(
                        stickerset=InputStickerSetID(id=stickerset_id, access_hash=stickerset_access_hash),
                        hash=0
                    ))
                    if patterns_set and patterns_set.documents:
                        print(f"   ✓ Найдено паттернов в стикерпаке: {len(patterns_set.documents)}")
                        doc_to_emoji = {}
                        for pack in getattr(patterns_set, 'packs', []):
                            emoji = pack.emoticon
                            for doc_id in pack.documents:
                                doc_to_emoji[doc_id] = emoji
                        
                        for doc in patterns_set.documents:
                            pattern_name = doc_to_emoji.get(doc.id, f"Pattern_{doc.id}")
                            patterns.append({
                                'name': pattern_name,
                                'rarity_permille': 0,
                                'sticker': doc
                            })
            except Exception as e:
                print(f"   ⚠ Не удалось загрузить стикерпак: {e}")
        
        # Если стикерпак не найден, используем sample_attributes
        if not patterns:
            print("   ℹ Стикерпак не найден, используем sample_attributes...")
            for attr in sample_attrs:
                attr_type = type(attr).__name__
                if 'Pattern' in attr_type or 'StarGiftAttributePattern' in attr_type:
                    patterns.append({
                        'name': getattr(attr, 'name', None),
                        'rarity_permille': getattr(attr, 'rarity_permille', 0),
                        'sticker': getattr(attr, 'document', None)
                    })
        
        if not patterns:
            print("   ✗ Паттерны не найдены")
            return
        
        print(f"   ✓ Найдено паттернов: {len(patterns)}")
        
        # Показываем список паттернов
        print("\n📋 Список паттернов:")
        print("-" * 60)
        for i, p in enumerate(patterns, 1):
            print(f"  {i}. {p['name']} (редкость: {p['rarity_permille']}‰)")
        print("-" * 60)
        
        # Запрашиваем GiftId
        print("\nВведите GiftId для которого применить эти паттерны:")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("   ✗ Введите число")
            return
        
        # Проверяем подарок
        col = self.db["stargifts"]
        existing_gift = col.find_one({"Id": target_gift_id})
        if not existing_gift:
            print(f"   ⚠ Подарок с ID {target_gift_id} не найден в базе")
            confirm = input("Продолжить всё равно? (y/n): ").strip().lower()
            if confirm not in ['y', 'yes', 'д', 'да']:
                print("   Отменено")
                return
        else:
            gift_title = existing_gift.get('Title', f"ID: {target_gift_id}")
            print(f"   ✓ Найден подарок: {gift_title}")
        
        # Импортируем паттерны
        print(f"\n📤 Импорт паттернов для GiftId {target_gift_id}...")
        patterns_col = self.db["stargift_patterns"]
        success_count = 0
        
        for i, pattern in enumerate(patterns, 1):
            print(f"\n[{i}/{len(patterns)}] {pattern['name']}")
            
            if not pattern.get('sticker'):
                print("   ⚠ Нет стикера, пропускаем")
                continue
            
            print("   📤 Загрузка стикера...")
            sticker_data = await self.download_and_upload_document(
                pattern['sticker'],
                f"pattern_{pattern['name']}"
            )
            
            if not sticker_data:
                print("   ✗ Не удалось загрузить стикер")
                continue
            
            # Формируем документ паттерна
            pattern_doc = {
                "name": pattern['name'],
                "rarityPermille": pattern['rarity_permille'],
                "GiftId": target_gift_id,
                "Sticker": {
                    "DocumentId": sticker_data["DocumentId"],
                    "AccessHash": sticker_data["AccessHash"],
                    "FileReferenceBase64": sticker_data["FileReferenceBase64"],
                    "Date": sticker_data["Date"],
                    "DcId": sticker_data["DcId"],
                    "MimeType": sticker_data["MimeType"],
                    "Size": sticker_data["Size"]
                }
            }
            
            patterns_col.insert_one(pattern_doc)
            success_count += 1
            print(f"   ✓ Паттерн сохранён: {pattern['name']} (редкость: {pattern['rarity_permille']}‰)")
        
        print("\n" + "=" * 60)
        print(f"✅ Импортировано паттернов: {success_count}/{len(patterns)}")
        print(f"   GiftId: {target_gift_id}")
        print("=" * 60)

    # ========================================================================
    # ИМПОРТ ФОНОВ (BACKDROPS) УЛУЧШЕНИЙ ПОДАРКОВ
    # ========================================================================
    
    async def import_gift_upgrade_backdrops(self):
        """Импорт фонов (backdrops) улучшений подарков из Telegram через GetStarGiftUpgradePreview"""
        print("\n🌈 Импорт фонов (backdrops) улучшений подарков")
        print("=" * 60)
        
        # Получаем все подарки из Telegram
        print("\n📥 Загрузка списка подарков из Telegram...")
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
        except Exception as e:
            print(f"   ✗ Ошибка при получении подарков: {e}")
            return
        
        if not hasattr(result, 'gifts') or not result.gifts:
            print("   ✗ Подарки не найдены")
            return
        
        gifts = result.gifts
        
        # Фильтруем только улучшаемые подарки
        upgradeable_gifts = []
        for gift in gifts:
            upgrade_stars = getattr(gift, 'upgrade_stars', None)
            if upgrade_stars and upgrade_stars > 0:
                gift_id = getattr(gift, 'id', None)
                title = getattr(gift, 'title', None)
                stars = getattr(gift, 'stars', 0)
                upgradeable_gifts.append({
                    'index': len(upgradeable_gifts),
                    'id': gift_id,
                    'title': title,
                    'stars': stars,
                    'upgrade_stars': upgrade_stars,
                    'original': gift
                })
        
        if not upgradeable_gifts:
            print("   ✗ Улучшаемые подарки не найдены")
            return
        
        print(f"   ✓ Найдено улучшаемых подарков: {len(upgradeable_gifts)}")
        
        # Показываем список
        print("\n📋 Список улучшаемых подарков:")
        print("-" * 60)
        for g in upgradeable_gifts:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name} - {g['stars']}⭐")
        print("-" * 60)
        
        # Выбор подарка
        print("\nВведите номер подарка для получения ВСЕХ его фонов:")
        try:
            num = int(input("Номер: ").strip())
            if num < 1 or num > len(upgradeable_gifts):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        selected_gift = upgradeable_gifts[num - 1]
        tg_gift_id = selected_gift['id']
        display_name = selected_gift['title'] if selected_gift['title'] else f"ID: {tg_gift_id}"
        print(f"\n   ✓ Выбран подарок: {display_name}")
        
        # Используем GetStarGiftUpgradePreview для получения атрибутов
        print("\n📥 Загрузка фонов через GetStarGiftUpgradePreview...")
        try:
            preview_result = await self.client(GetStarGiftUpgradePreviewRequest(
                gift_id=tg_gift_id
            ))
        except Exception as e:
            print(f"   ✗ Ошибка при получении атрибутов: {e}")
            return
        
        if not preview_result:
            print("   ✗ Результат не получен")
            return
        
        sample_attrs = getattr(preview_result, 'sample_attributes', [])
        print(f"   ✓ Получено sample_attributes: {len(sample_attrs)}")
        
        # Фильтруем только фоны
        backdrops = []
        for attr in sample_attrs:
            attr_type = type(attr).__name__
            
            if 'Backdrop' not in attr_type and 'StarGiftAttributeBackdrop' not in attr_type:
                continue
            
            backdrop_name = getattr(attr, 'name', None)
            rarity_permille = getattr(attr, 'rarity_permille', 0)
            center_color = getattr(attr, 'center_color', 0)
            edge_color = getattr(attr, 'edge_color', 0)
            pattern_color = getattr(attr, 'pattern_color', 0)
            text_color = getattr(attr, 'text_color', 0)
            
            backdrops.append({
                'name': backdrop_name,
                'rarity_permille': rarity_permille,
                'center_color': center_color,
                'edge_color': edge_color,
                'pattern_color': pattern_color,
                'text_color': text_color
            })
        
        if not backdrops:
            print("   ✗ Фоны не найдены")
            return
        
        print(f"   ✓ Найдено фонов: {len(backdrops)}")
        
        # Показываем список фонов
        print("\n📋 Список фонов:")
        print("-" * 60)
        for i, b in enumerate(backdrops, 1):
            print(f"  {i}. {b['name']} (редкость: {b['rarity_permille']}‰)")
        print("-" * 60)
        
        # Запрашиваем GiftId
        print("\nВведите GiftId для которого применить эти фоны:")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("   ✗ Введите число")
            return
        
        # Проверяем подарок
        col = self.db["stargifts"]
        existing_gift = col.find_one({"Id": target_gift_id})
        if not existing_gift:
            print(f"   ⚠ Подарок с ID {target_gift_id} не найден в базе")
            confirm = input("Продолжить всё равно? (y/n): ").strip().lower()
            if confirm not in ['y', 'yes', 'д', 'да']:
                print("   Отменено")
                return
        else:
            gift_title = existing_gift.get('Title', f"ID: {target_gift_id}")
            print(f"   ✓ Найден подарок: {gift_title}")
        
        # Импортируем фоны
        print(f"\n📤 Импорт фонов для GiftId {target_gift_id}...")
        backdrops_col = self.db["backdrops"]
        success_count = 0
        
        for i, backdrop in enumerate(backdrops, 1):
            print(f"\n[{i}/{len(backdrops)}] {backdrop['name']}")
            
            # Формируем документ фона
            backdrop_doc = {
                "name": backdrop['name'],
                "rarityPermille": backdrop['rarity_permille'],
                "GiftId": target_gift_id,
                "centerColor": backdrop['center_color'],
                "edgeColor": backdrop['edge_color'],
                "patternColor": backdrop['pattern_color'],
                "textColor": backdrop['text_color']
            }
            
            backdrops_col.insert_one(backdrop_doc)
            success_count += 1
            print(f"   ✓ Фон сохранён: {backdrop['name']} (редкость: {backdrop['rarity_permille']}‰)")
        
        print("\n" + "=" * 60)
        print(f"✅ Импортировано фонов: {success_count}/{len(backdrops)}")
        print(f"   GiftId: {target_gift_id}")
        print("=" * 60)

    # ========================================================================
    # ИМПОРТ ВСЕХ УЛУЧШЕНИЙ ПОДАРКА (МОДЕЛИ + ПАТТЕРНЫ + ФОНЫ)
    # ========================================================================
    
    async def _load_stickerset_by_shortname(self, short_name: str) -> Optional[List]:
        """Загружает стикерпак по short_name и возвращает список документов"""
        try:
            sticker_set = await self.client(GetStickerSetRequest(
                stickerset=InputStickerSetShortName(short_name=short_name),
                hash=0
            ))
            if sticker_set and sticker_set.documents:
                # Создаём маппинг document_id -> emoji из packs
                doc_to_emoji = {}
                for pack in getattr(sticker_set, 'packs', []):
                    emoji = pack.emoticon
                    for doc_id in pack.documents:
                        doc_to_emoji[doc_id] = emoji
                
                result = []
                for doc in sticker_set.documents:
                    name = doc_to_emoji.get(doc.id, f"Item_{doc.id}")
                    result.append({
                        'name': name,
                        'document': doc
                    })
                return result
        except Exception as e:
            print(f"   ⚠ Не удалось загрузить стикерпак {short_name}: {e}")
        return None
    
    async def import_all_gift_upgrades(self):
        """Импорт всех улучшений подарка через многократный вызов GetStarGiftUpgradePreview"""
        print("\n🎁 Импорт ВСЕХ улучшений подарка (модели + паттерны + фоны)")
        print("=" * 60)
        
        # Получаем все подарки из Telegram
        print("\n📥 Загрузка списка подарков из Telegram...")
        try:
            result = await self.client(GetStarGiftsRequest(hash=0))
        except Exception as e:
            print(f"   ✗ Ошибка при получении подарков: {e}")
            return
        
        if not hasattr(result, 'gifts') or not result.gifts:
            print("   ✗ Подарки не найдены")
            return
        
        gifts = result.gifts
        
        # Фильтруем только улучшаемые подарки
        upgradeable_gifts = []
        for gift in gifts:
            upgrade_stars = getattr(gift, 'upgrade_stars', None)
            if upgrade_stars and upgrade_stars > 0:
                gift_id = getattr(gift, 'id', None)
                title = getattr(gift, 'title', None)
                stars = getattr(gift, 'stars', 0)
                upgradeable_gifts.append({
                    'index': len(upgradeable_gifts),
                    'id': gift_id,
                    'title': title,
                    'stars': stars,
                    'upgrade_stars': upgrade_stars,
                    'original': gift
                })
        
        if not upgradeable_gifts:
            print("   ✗ Улучшаемые подарки не найдены")
            return
        
        print(f"   ✓ Найдено улучшаемых подарков: {len(upgradeable_gifts)}")
        
        # Показываем список
        print("\n📋 Список улучшаемых подарков:")
        print("-" * 60)
        for g in upgradeable_gifts:
            display_name = g['title'] if g['title'] else f"ID: {g['id']}"
            print(f"  {g['index'] + 1}. {display_name} - {g['stars']}⭐ (апгрейд: {g['upgrade_stars']}⭐)")
        print("-" * 60)
        
        # Выбор подарка
        print("\nВведите номер подарка для импорта ВСЕХ его улучшений:")
        try:
            num = int(input("Номер: ").strip())
            if num < 1 or num > len(upgradeable_gifts):
                print("   ✗ Неверный номер")
                return
        except ValueError:
            print("   ✗ Введите число")
            return
        
        selected_gift = upgradeable_gifts[num - 1]
        tg_gift_id = selected_gift['id']
        display_name = selected_gift['title'] if selected_gift['title'] else f"ID: {tg_gift_id}"
        print(f"\n   ✓ Выбран подарок: {display_name}")
        
        # Запрашиваем GiftId для базы данных
        print("\nВведите GiftId для которого применить ВСЕ улучшения:")
        print("(это ID подарка в вашей базе stargifts)")
        try:
            target_gift_id = int(input("GiftId: ").strip())
        except ValueError:
            print("   ✗ Введите число")
            return
        
        # Проверяем подарок в базе
        col = self.db["stargifts"]
        existing_gift = col.find_one({"Id": target_gift_id})
        if not existing_gift:
            print(f"   ⚠ Подарок с ID {target_gift_id} не найден в базе")
            confirm = input("Продолжить всё равно? (y/n): ").strip().lower()
            if confirm not in ['y', 'yes', 'д', 'да']:
                print("   Отменено")
                return
        else:
            gift_title = existing_gift.get('Title', f"ID: {target_gift_id}")
            print(f"   ✓ Найден подарок в базе: {gift_title}")
        
        # Сбор атрибутов многократным вызовом Preview
        print("\n📥 Сбор ВСЕХ атрибутов через многократный вызов GetStarGiftUpgradePreview...")
        print("   (Это может занять несколько минут)")
        
        # Словари для уникальных атрибутов (по имени)
        models_dict = {}  # name -> {name, rarity_permille, document}
        patterns_dict = {}
        backdrops_dict = {}
        
        max_calls = 500  # Максимум вызовов
        no_new_streak = 0  # Счётчик вызовов без новых атрибутов
        stop_after_no_new = 50  # Остановиться после N вызовов без новых
        
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
                    
                    if 'Model' in attr_type:
                        if name not in models_dict and name.lower() != 'original':
                            models_dict[name] = {
                                'name': name,
                                'rarity_permille': getattr(attr, 'rarity_permille', 0),
                                'sticker': getattr(attr, 'document', None)
                            }
                            found_new = True
                    elif 'Pattern' in attr_type:
                        if name not in patterns_dict:
                            patterns_dict[name] = {
                                'name': name,
                                'rarity_permille': getattr(attr, 'rarity_permille', 0),
                                'sticker': getattr(attr, 'document', None)
                            }
                            found_new = True
                    elif 'Backdrop' in attr_type:
                        if name not in backdrops_dict:
                            backdrops_dict[name] = {
                                'name': name,
                                'rarity_permille': getattr(attr, 'rarity_permille', 0),
                                'center_color': getattr(attr, 'center_color', 0),
                                'edge_color': getattr(attr, 'edge_color', 0),
                                'pattern_color': getattr(attr, 'pattern_color', 0),
                                'text_color': getattr(attr, 'text_color', 0)
                            }
                            found_new = True
                
                if found_new:
                    no_new_streak = 0
                else:
                    no_new_streak += 1
                
                # Прогресс каждые 10 вызовов
                if call_num % 10 == 0:
                    print(f"   [{call_num}/{max_calls}] Моделей: {len(models_dict)}, Паттернов: {len(patterns_dict)}, Фонов: {len(backdrops_dict)} (без новых: {no_new_streak})")
                
                # Остановка если долго нет новых
                if no_new_streak >= stop_after_no_new:
                    print(f"\n   ✓ Остановка: {stop_after_no_new} вызовов без новых атрибутов")
                    break
                
                # Небольшая пауза чтобы не флудить
                await asyncio.sleep(0.1)
                
            except Exception as e:
                print(f"   ⚠ Ошибка на вызове {call_num}: {e}")
                await asyncio.sleep(1)
        
        # Конвертируем в списки
        all_models = list(models_dict.values())
        all_patterns = list(patterns_dict.values())
        all_backdrops = list(backdrops_dict.values())
        
        print(f"\n   📊 Найдено:")
        print(f"      Моделей: {len(all_models)}")
        print(f"      Паттернов: {len(all_patterns)}")
        print(f"      Фонов: {len(all_backdrops)}")
        
        # ===== ИМПОРТ МОДЕЛЕЙ =====
        if all_models:
            print(f"\n{'='*60}")
            print(f"📤 Импорт моделей для GiftId {target_gift_id}...")
            models_col = self.db["stargift_models"]
            models_success = 0
            
            for i, model in enumerate(all_models, 1):
                if not model.get('sticker'):
                    print(f"   [{i}/{len(all_models)}] {model['name']}: ⚠ нет стикера")
                    continue
                
                print(f"   [{i}/{len(all_models)}] {model['name']}...")
                sticker_data = await self.download_and_upload_document(
                    model['sticker'],
                    f"model_{model['name']}"
                )
                
                if sticker_data:
                    model_doc = {
                        "name": model['name'],
                        "rarityPermille": model['rarity_permille'],
                        "GiftId": target_gift_id,
                        "Sticker": {
                            "DocumentId": sticker_data["DocumentId"],
                            "AccessHash": sticker_data["AccessHash"],
                            "FileReferenceBase64": sticker_data["FileReferenceBase64"],
                            "Date": sticker_data["Date"],
                            "DcId": sticker_data["DcId"],
                            "MimeType": sticker_data["MimeType"],
                            "Size": sticker_data["Size"]
                        }
                    }
                    models_col.insert_one(model_doc)
                    models_success += 1
            
            print(f"   ✓ Импортировано моделей: {models_success}/{len(all_models)}")
        
        # ===== ИМПОРТ ПАТТЕРНОВ =====
        if all_patterns:
            print(f"\n{'='*60}")
            print(f"📤 Импорт паттернов для GiftId {target_gift_id}...")
            patterns_col = self.db["stargift_patterns"]
            patterns_success = 0
            
            for i, pattern in enumerate(all_patterns, 1):
                if not pattern.get('sticker'):
                    print(f"   [{i}/{len(all_patterns)}] {pattern['name']}: ⚠ нет стикера")
                    continue
                
                print(f"   [{i}/{len(all_patterns)}] {pattern['name']}...")
                sticker_data = await self.download_and_upload_document(
                    pattern['sticker'],
                    f"pattern_{pattern['name']}"
                )
                
                if sticker_data:
                    pattern_doc = {
                        "name": pattern['name'],
                        "rarityPermille": pattern['rarity_permille'],
                        "GiftId": target_gift_id,
                        "Sticker": {
                            "DocumentId": sticker_data["DocumentId"],
                            "AccessHash": sticker_data["AccessHash"],
                            "FileReferenceBase64": sticker_data["FileReferenceBase64"],
                            "Date": sticker_data["Date"],
                            "DcId": sticker_data["DcId"],
                            "MimeType": sticker_data["MimeType"],
                            "Size": sticker_data["Size"]
                        }
                    }
                    patterns_col.insert_one(pattern_doc)
                    patterns_success += 1
            
            print(f"   ✓ Импортировано паттернов: {patterns_success}/{len(all_patterns)}")
        
        # ===== ИМПОРТ ФОНОВ =====
        if all_backdrops:
            print(f"\n{'='*60}")
            print(f"📤 Импорт фонов для GiftId {target_gift_id}...")
            backdrops_col = self.db["backdrops"]
            backdrops_success = 0
            
            for i, backdrop in enumerate(all_backdrops, 1):
                print(f"   [{i}/{len(all_backdrops)}] {backdrop['name']}...")
                backdrop_doc = {
                    "name": backdrop['name'],
                    "rarityPermille": backdrop['rarity_permille'],
                    "GiftId": target_gift_id,
                    "centerColor": backdrop['center_color'],
                    "edgeColor": backdrop['edge_color'],
                    "patternColor": backdrop['pattern_color'],
                    "textColor": backdrop['text_color']
                }
                backdrops_col.insert_one(backdrop_doc)
                backdrops_success += 1
            
            print(f"   ✓ Импортировано фонов: {backdrops_success}/{len(all_backdrops)}")
        
        # Итог
        print("\n" + "=" * 60)
        print(f"✅ Импорт завершён для GiftId {target_gift_id}!")
        print(f"   Моделей: {len([m for m in all_models if m.get('sticker')])}")
        print(f"   Паттернов: {len([p for p in all_patterns if p.get('sticker')])}")
        print(f"   Фонов: {len(all_backdrops)}")
        print("=" * 60)

    async def debug_upgrade_preview(self):
        """DEBUG: Просмотр GetStarGiftUpgradePreview"""
        print("\n🔍 DEBUG: GetStarGiftUpgradePreview")
        print("=" * 60)
        
        # Получаем подарки
        print("\n📥 Загрузка списка подарков...")
        result = await self.client(GetStarGiftsRequest(hash=0))
        gifts = result.gifts
        
        # Фильтруем улучшаемые
        upgradeable = []
        for gift in gifts:
            if getattr(gift, 'upgrade_stars', 0) > 0:
                upgradeable.append(gift)
        
        print(f"   ✓ Улучшаемых подарков: {len(upgradeable)}")
        
        # Показываем список
        for i, g in enumerate(upgradeable[:20], 1):
            title = getattr(g, 'title', None) or f"ID: {g.id}"
            print(f"  {i}. {title}")
        
        try:
            num = int(input("\nНомер подарка: ").strip())
            gift = upgradeable[num - 1]
        except:
            print("   ✗ Неверный номер")
            return
        
        gift_id = gift.id
        print(f"\n📥 Вызов GetStarGiftUpgradePreview для gift_id={gift_id}...")
        
        try:
            preview = await self.client(GetStarGiftUpgradePreviewRequest(gift_id=gift_id))
        except Exception as e:
            print(f"   ✗ Ошибка: {e}")
            return
        
        print(f"\n{'='*60}")
        print(f"Тип ответа: {type(preview).__name__}")
        print(f"{'='*60}")
        
        # Показываем все поля ответа
        for field in dir(preview):
            if field.startswith('_'):
                continue
            try:
                val = getattr(preview, field)
                if callable(val):
                    continue
                print(f"\n📦 {field}: {type(val).__name__}")
                
                if isinstance(val, (list, tuple)):
                    print(f"   Длина: {len(val)}")
                    for i, item in enumerate(val[:10]):
                        item_type = type(item).__name__
                        print(f"\n   [{i}] {item_type}:")
                        
                        # Показываем поля каждого атрибута
                        for attr in dir(item):
                            if attr.startswith('_'):
                                continue
                            try:
                                attr_val = getattr(item, attr)
                                if callable(attr_val):
                                    continue
                                
                                # Особое внимание на document и stickerset
                                if attr == 'document' and attr_val:
                                    print(f"       .document: <Document id={attr_val.id}>")
                                    # Ищем stickerset в атрибутах документа
                                    doc_attrs = getattr(attr_val, 'attributes', [])
                                    for da in doc_attrs:
                                        da_type = type(da).__name__
                                        print(f"          attr: {da_type}")
                                        ss = getattr(da, 'stickerset', None)
                                        if ss:
                                            print(f"          🎯 STICKERSET: id={getattr(ss, 'id', '?')}, access_hash={getattr(ss, 'access_hash', '?')}")
                                            # Пробуем получить short_name
                                            for ss_attr in dir(ss):
                                                if not ss_attr.startswith('_'):
                                                    try:
                                                        ss_val = getattr(ss, ss_attr)
                                                        if not callable(ss_val):
                                                            print(f"             .{ss_attr} = {ss_val}")
                                                    except:
                                                        pass
                                else:
                                    print(f"       .{attr} = {attr_val}")
                            except:
                                pass
                else:
                    print(f"   Значение: {val}")
            except:
                pass
        
        print(f"\n{'='*60}")
        input("\nНажмите Enter...")

    async def close(self):
        """Закрытие соединений"""
        if self.client:
            await self.client.disconnect()
        if self.mongo_client:
            self.mongo_client.close()


def show_menu():
    """Показывает главное меню"""
    print("\n" + "=" * 60)
    print("       TELEGRAM IMPORTER - MyTelegram")
    print("=" * 60)
    print("\nВыберите действие:")
    print("  1. Импорт реакций")
    print("  2. Импорт стикер пака")
    print("  3. Импорт эмодзи пака")
    print("  4. Импорт подарков (Star Gifts)")
    print("  5. Импорт ВСЕХ улучшений подарка (модели + паттерны + фоны)")
    print("  ---")
    print("  6. Импорт только моделей улучшений")
    print("  7. Импорт только паттернов улучшений")
    print("  8. Импорт только фонов (backdrops)")
    print("  ---")
    print("  9. [DEBUG] Просмотр GetStarGiftUpgradePreview")
    print("  0. Выход")
    print()
    return input("Ваш выбор: ").strip()


async def main():
    importer = TelegramImporter()
    
    try:
        await importer.connect()
        
        while True:
            choice = show_menu()
            
            if choice == "1":
                await importer.import_reactions()
            elif choice == "2":
                await importer.import_sticker_pack(is_emoji=False)
            elif choice == "3":
                await importer.import_sticker_pack(is_emoji=True)
            elif choice == "4":
                await importer.import_star_gifts()
            elif choice == "5":
                await importer.import_all_gift_upgrades()
            elif choice == "6":
                await importer.import_gift_upgrade_models()
            elif choice == "7":
                await importer.import_gift_upgrade_patterns()
            elif choice == "8":
                await importer.import_gift_upgrade_backdrops()
            elif choice == "9":
                await importer.debug_upgrade_preview()
            elif choice == "0":
                print("\n👋 До свидания!")
                break
            else:
                print("\n⚠ Неверный выбор, попробуйте снова")
                
    except Exception as e:
        print(f"\n❌ Ошибка: {e}")
        raise
    finally:
        await importer.close()


if __name__ == "__main__":
    # Проверяем конфигурацию
    if API_ID == 123456 or API_HASH == "your_api_hash_here":
        print("=" * 60)
        print("⚠️  ВНИМАНИЕ: Заполни конфигурацию в начале файла!")
        print("=" * 60)
        print("\n1. Получи API_ID и API_HASH на https://my.telegram.org")
        print("2. Заполни MONGO_URI, MINIO_* параметры")
        print("3. Запусти скрипт снова")
        print()
    else:
        asyncio.run(main())
