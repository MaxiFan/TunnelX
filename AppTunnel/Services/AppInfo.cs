namespace AppTunnel.Services;

public static class AppInfo
{
    public const string AppName = "TunnelX";
    public const string CreatorName = "MaxFan";
    public const string GitHubUrl = "https://github.com/MaxiFan/TunnelX";
    public const string LicenseName = "GPL-3.0-or-later";
    public const string PayPalEmail = "gallafan@gmail.com";
    public const string PayPalDonateUrl = "https://www.paypal.com/donate/?business=gallafan%40gmail.com&currency_code=USD";
    public const string CryptoDonationText =
        "TRON / USDT-TRC20: TNWV867fQDT6zpLunHgbeMjrN6ic63LQSu\n" +
        "Bitcoin: bc1qgx3g47c458fu6smnpqpu0l05hha82rq2xjet4y\n" +
        "Ethereum / USDT-ERC20: 0x72d94Bb250E8802441a0ED05686Ee925BC99Fef5\n" +
        "TON: UQD65oL2Vu2OJDSrwQ0wLLSw3g668SREMJ3VPW9k8b6Sy-Yf\n" +
        "BNB Smart Chain: 0xE2a5b01cE2b3713D435Bc16d92eAdd88A82159f0\n" +
        "Dogecoin: DSZRNY65yF679uvjAh6sUAt6YiEEQHwKGb";

    public static string VersionText =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                   .GetName().Version?.ToString(3) ?? "1.0.0");

    public static string ReleaseText => $"{AppName} {VersionText}";
    public static string CreatorText => $"ساخته شده توسط {CreatorName}";
}
