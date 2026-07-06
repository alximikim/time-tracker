# Деплой backend (Google Apps Script Web App)

## 1. Вставить код

1. Открыть таблицу [«Система учета времени ОП»](https://docs.google.com/spreadsheets/d/107351O09XVj8TrWHF5lOTPocDpISgytuTv9iW5aHtyM).
2. Расширения → Apps Script.
3. Удалить содержимое `Code.gs` по умолчанию, вставить содержимое файла
   [`Code.gs`](./Code.gs) из этого репозитория.
4. Сохранить (значок дискеты / Ctrl+S).

## 2. Настроить Script Properties

В редакторе Apps Script: значок шестерёнки слева → **Свойства проекта** →
вкладка **Свойства скрипта** → добавить:

| Свойство | Значение | Зачем |
|---|---|---|
| `SHARED_SECRET` | любая длинная случайная строка | клиент передаёт её в каждом запросе; без совпадения сервер отклоняет запись |
| `TARGET_SHEET_NAME` | `Часы` | на каком листе писать сессии; боевое значение, использовалось `Тест` на время проверки |

Тот же `SHARED_SECRET` нужно будет указать в конфиге клиента (`ApiSettings.cs`,
константа `SharedToken`).

## 3. Задеплоить как Web App

1. Кнопка **Развернуть** (Deploy) → **Новое развёртывание** (New deployment).
2. Тип: **Веб-приложение** (Web app).
3. **Execute as**: **Me** (важно — тогда скрипт пишет в таблицу от имени
   владельца, клиентам не нужны отдельные права на таблицу).
4. **Who has access**: **Anyone** (в рамках Apps Script это не даёт доступ к
   таблице напрямую — только к этому конкретному endpoint'у, который сам
   проверяет `SHARED_SECRET`).
5. Развернуть, скопировать URL вида
   `https://script.google.com/macros/s/XXXXX/exec` — это и есть `ApiUrl` для
   клиента.

## 4. Проверка вручную (без клиента)

```powershell
$url = "https://script.google.com/macros/s/XXXXX/exec"

# health-check
Invoke-RestMethod -Uri $url -Method Get

# тестовый старт сессии
$body = @{
  token = "<SHARED_SECRET>"
  employeeId = "test"
  employeeName = "Тестовый Сотрудник"
  eventType = "start"
  clientTimestamp = (Get-Date).ToString("o")
  sessionId = [guid]::NewGuid().ToString()
  machineId = "TEST-PC"
} | ConvertTo-Json

Invoke-RestMethod -Uri $url -Method Post -Body $body -ContentType "application/json"
```

После этого на листе «Тест» должна появиться строка со статусом
«в процессе». Повторная отправка того же `sessionId` с `eventType: start`
не должна создавать вторую строку.

## 5. Обновление кода после правок

Apps Script не подхватывает изменения в `/exec` автоматически — после любой
правки `Code.gs` нужно: **Развернуть → Управление развёртываниями → значок
редактирования у активного развёртывания → Версия: Новая версия → Развернуть**.

## 6. Переход с тестового листа на боевой

Уже сделано — `TARGET_SHEET_NAME` в Script Properties стоит `Часы`, система
пишет в боевой лист. При необходимости вернуться на тестовый режим — сменить
`TARGET_SHEET_NAME` обратно на `Тест` (без передеплоя, свойства скрипта
читаются в рантайме).
