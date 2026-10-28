using Microsoft.Extensions.Logging;
using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Wpf;

namespace WindowVeil.RetroCapDemo;

/// <summary>
/// Command line:
/// <code>
/// WindowVeil.RetroCapDemo --retrocap C:\path\RetroCap.exe [--evidence artifacts\screenshots] [--models models] [--require-ocr]
/// WindowVeil.RetroCapDemo --attach            (masks an already running RetroCap until you press Enter)
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));

        if (args.Contains("--attach")) return await AttachInteractiveAsync(loggerFactory);

        var exe = Arg("--retrocap") ?? Environment.GetEnvironmentVariable("WINDOWVEIL_RETROCAP_EXE");
        if (exe is null)
        {
            Console.Error.WriteLine("Usage: WindowVeil.RetroCapDemo --retrocap <RetroCap.exe> [--evidence <dir>] [--models <dir>] [--require-ocr]");
            Console.Error.WriteLine("       WindowVeil.RetroCapDemo --attach");
            Console.Error.WriteLine("Get the verified RetroCap v1.0 binary with tools/get-retrocap.ps1.");
            return 2;
        }
        var manifest = await RetroCapScenarioRunner.RunAsync(new RetroCapScenarioOptions
        {
            RetroCapExe = exe,
            EvidenceDir = Path.GetFullPath(Arg("--evidence") ?? Path.Combine("artifacts", "screenshots")),
            OcrModelsDir = Arg("--models") ?? Environment.GetEnvironmentVariable("WINDOWVEIL_OCR_MODELS"),
            RequireOcr = args.Contains("--require-ocr"),
            CommitSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            Version = typeof(WindowVeilEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            LoggerFactory = loggerFactory,
            Log = Console.WriteLine,
        });
        return manifest.Result == "passed" ? 0 : 1;
    }

    private static async Task<int> AttachInteractiveAsync(ILoggerFactory loggerFactory)
    {
        await using var engine = WindowVeilEngine.CreateBuilder()
            .UseLoggerFactory(loggerFactory)
            .UseWindowsDesktop()
            .UseWpfOverlays()
            .Build();
        var selector = WindowSelector.ByProcessName("RetroCap").WithTitle(RetroCapHost.MainWindowTitle);
        await using var session = await engine.AttachAsync(selector, new SessionOptions { AttachTimeout = TimeSpan.FromSeconds(30) });
        session.AddRule(MaskRule.Create(
            ElementLocator.ByAutomationId(RetroCapScenarioRunner.StartupCheckId)
                .Or(ElementLocator.ByName(RetroCapScenarioRunner.StartupText))
                .Or(ElementLocator.ByName("Start with Windows")), // label used by RetroCap v1.0
            MaskAppearance.Solid("#202020"), id: "startup"));
        await session.StartAsync();
        Console.WriteLine($"Native mask: {session.GetRuleStatus("startup")}");
        Console.WriteLine("Press Enter to switch to the WPF replacement...");
        Console.ReadLine();
        session.UpdateRule(session.GetRule("startup")! with { Appearance = WpfAppearance.Create(() => new HiddenSettingOverlay()) });
        await session.RefreshAsync();
        Console.WriteLine($"WPF replacement: {session.GetRuleStatus("startup")}");
        Console.WriteLine("Move or resize RetroCap; the overlay follows. Press Enter to remove the overlay...");
        Console.ReadLine();
        return 0;
    }
}
