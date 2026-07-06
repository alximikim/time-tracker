namespace TimeTracker;

/// <summary>
/// Адрес backend'а и общий секрет. Для внутреннего инструмента на ~15
/// человек проще зашить в сборку, чем просить каждого сотрудника вставлять
/// URL/токен вручную — см. backend/SETUP.md, откуда берутся эти значения.
/// </summary>
public static class ApiSettings
{
    public const string ApiUrl = "https://script.google.com/macros/s/AKfycbyVRS-9ZESM1Mn4QGiaLy1w7JUU_Ch5isENZ_iNVuk5xjQQUcElhlcF8hgKR0SySMic/exec";

    public const string SharedToken = "zMmRIQV8NjSy0YOx73dTnwX62EWhZpBu9rkCagfs";
}
