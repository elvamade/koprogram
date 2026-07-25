# create_session.py
from telethon.sync import TelegramClient
from telethon.errors import SessionPasswordNeededError

API_ID = 23268210
API_HASH = "5bdfdbcfc0397f41ec13edb8720b52ea"
SESSION_NAME = "gift_importer"  # создаст my_session.session

def main():
    phone = input("Телефон (+7...): ").strip()

    with TelegramClient(SESSION_NAME, API_ID, API_HASH) as client:
        client.send_code_request(phone)
        code = input("Код из Telegram/SMS: ").strip()

        try:
            client.sign_in(phone=phone, code=code)
        except SessionPasswordNeededError:
            password = input("Пароль 2FA: ").strip()
            client.sign_in(password=password)

    print(f"Готово: создан файл {SESSION_NAME}.session")

if __name__ == "__main__":
    main()
