/**
 * Time Tracker — Google Apps Script backend
 *
 * Bound-скрипт таблицы "График МП". Принимает события старт/стоп/heartbeat
 * от клиентских трей-приложений сотрудников (POST) и ведёт лист сессий,
 * не трогая ручной табель HR на листе "МП".
 *
 * Деплой и настройка — см. SETUP.md рядом с этим файлом.
 */

var HEADERS = [
  "Дата", "Сотрудник", "ID сотрудника", "Начало", "Конец",
  "Часов", "Статус", "MachineId", "SessionId", "Обновлено"
];

var COL = {
  DATE: 1, EMPLOYEE_NAME: 2, EMPLOYEE_ID: 3, START: 4, END: 5,
  HOURS: 6, STATUS: 7, MACHINE_ID: 8, SESSION_ID: 9, UPDATED: 10
};

function doGet(e) {
  return _json({ ok: true, message: "TimeTracker backend running" });
}

function doPost(e) {
  var lock = LockService.getScriptLock();
  try {
    lock.waitLock(10000);
  } catch (err) {
    return _json({ ok: false, message: "Сервер занят, попробуйте ещё раз" });
  }

  try {
    var payload;
    try {
      payload = JSON.parse(e.postData.contents);
    } catch (parseErr) {
      return _json({ ok: false, message: "Некорректный JSON" });
    }

    var expectedToken = PropertiesService.getScriptProperties().getProperty("SHARED_SECRET");
    if (!expectedToken || payload.token !== expectedToken) {
      return _json({ ok: false, message: "Неверный токен" });
    }

    if (!payload.sessionId || !payload.eventType) {
      return _json({ ok: false, message: "Не хватает sessionId/eventType" });
    }

    switch (payload.eventType) {
      case "start":
        return _json(_handleStart(payload));
      case "stop":
        return _json(_handleStop(payload));
      case "heartbeat":
        return _json(_handleHeartbeat(payload));
      default:
        return _json({ ok: false, message: "Неизвестный eventType: " + payload.eventType });
    }
  } catch (err) {
    return _json({ ok: false, message: "Ошибка сервера: " + err.message });
  } finally {
    lock.releaseLock();
  }
}

/**
 * eventType: "start" — создаёт строку сессии, если её ещё нет (idempotent —
 * повторная отправка после обрыва связи не плодит дубли).
 */
function _handleStart(payload) {
  var sheet = _ensureSheet();
  var row = _findRowBySessionId(sheet, payload.sessionId);
  if (row) {
    return { ok: true, message: "Сессия уже существует" };
  }

  var start = _parseDate(payload.clientTimestamp);
  var now = new Date();

  sheet.appendRow([
    Utilities.formatDate(start, Session.getScriptTimeZone(), "dd.MM.yyyy"),
    payload.employeeName || "",
    payload.employeeId || "",
    start,
    "",
    "",
    "в процессе",
    payload.machineId || "",
    payload.sessionId,
    now
  ]);
  SpreadsheetApp.flush();

  return { ok: true, message: "started" };
}

/**
 * eventType: "stop" — закрывает существующую строку сессии (или создаёт её
 * "на лету", если stop пришёл без предшествующего start — гонка/потеря start).
 * Если строка уже закрыта — no-op (идемпотентность).
 */
function _handleStop(payload) {
  var sheet = _ensureSheet();
  var row = _findRowBySessionId(sheet, payload.sessionId);
  var end = _parseDate(payload.clientTimestamp);
  var now = new Date();

  if (!row) {
    // stop без start — создаём строку целиком, чтобы данные не потерялись
    sheet.appendRow([
      Utilities.formatDate(end, Session.getScriptTimeZone(), "dd.MM.yyyy"),
      payload.employeeName || "",
      payload.employeeId || "",
      "",
      end,
      "",
      payload.stopStatus || "кнопка",
      payload.machineId || "",
      payload.sessionId,
      now
    ]);
    SpreadsheetApp.flush();
    return { ok: true, message: "stop без start — создана строка" };
  }

  var existingEnd = sheet.getRange(row, COL.END).getValue();
  if (existingEnd) {
    return { ok: true, message: "Сессия уже закрыта" };
  }

  var start = sheet.getRange(row, COL.START).getValue();
  var hours = start ? (end.getTime() - new Date(start).getTime()) / 3600000 : "";

  sheet.getRange(row, COL.END).setValue(end);
  sheet.getRange(row, COL.HOURS).setValue(hours ? Math.round(hours * 100) / 100 : "");
  sheet.getRange(row, COL.STATUS).setValue(payload.stopStatus || "кнопка");
  sheet.getRange(row, COL.UPDATED).setValue(now);
  SpreadsheetApp.flush();

  return { ok: true, message: "stopped" };
}

/**
 * eventType: "heartbeat" — только обновляет колонку "Обновлено" существующей
 * строки, для визуального контроля "не зависла ли открытая сессия".
 */
function _handleHeartbeat(payload) {
  var sheet = _ensureSheet();
  var row = _findRowBySessionId(sheet, payload.sessionId);
  if (!row) {
    return { ok: true, message: "Сессия не найдена, heartbeat пропущен" };
  }

  sheet.getRange(row, COL.UPDATED).setValue(new Date());
  SpreadsheetApp.flush();
  return { ok: true, message: "heartbeat" };
}

/**
 * Возвращает целевой лист, создавая его и шапку при первом обращении.
 * Имя листа берётся из Script Property TARGET_SHEET_NAME (по умолчанию "Тест").
 * Переключение Тест → Часы после проверки — смена этого свойства, без передеплоя.
 */
function _ensureSheet() {
  var ss = SpreadsheetApp.getActiveSpreadsheet();
  var name = PropertiesService.getScriptProperties().getProperty("TARGET_SHEET_NAME") || "Тест";
  var sheet = ss.getSheetByName(name);

  if (!sheet) {
    sheet = ss.insertSheet(name);
    sheet.appendRow(HEADERS);
    sheet.setFrozenRows(1);
    SpreadsheetApp.flush();
  }

  return sheet;
}

/**
 * Линейный поиск строки по SessionId (колонка I). Для ~15 сотрудников и
 * сессий в пределах месяца объём данных небольшой — полнотекстовый скан
 * колонки на каждый запрос вполне приемлем и не требует поддержки индекса.
 */
function _findRowBySessionId(sheet, sessionId) {
  var lastRow = sheet.getLastRow();
  if (lastRow < 2) return null;

  var ids = sheet.getRange(2, COL.SESSION_ID, lastRow - 1, 1).getValues();
  for (var i = 0; i < ids.length; i++) {
    if (ids[i][0] === sessionId) {
      return i + 2;
    }
  }
  return null;
}

function _parseDate(isoString) {
  var d = isoString ? new Date(isoString) : new Date();
  return isNaN(d.getTime()) ? new Date() : d;
}

function _json(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj))
    .setMimeType(ContentService.MimeType.JSON);
}
