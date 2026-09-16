// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using SharpEmu.GUI;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(SharpEmu.Gui.Tests.GuiTestAppBuilder))]

namespace SharpEmu.Gui.Tests;

/// <summary>
/// Reuses the real application class so theme/font XAML resources load exactly
/// as they do on a user's desktop.
/// </summary>
public static class GuiTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>
/// Boot smoke tests for the desktop frontend.
///
/// These exist because of a real incident: the round-2 Windows build shipped a
/// stale Avalonia-XAML-compiled <c>SharpEmu.GUI.dll</c> whose compiled XAML
/// predated the <c>RenderingBackendRow</c> options row, so
/// <see cref="MainWindow.InitializeLocalizedChoiceBoxes"/> dereferenced a
/// never-registered name and the GUI crashed with NullReferenceException before
/// showing a window. An incremental-build up-to-date check had silently skipped
/// the XAML recompile after the source tree was restored from an archive with
/// older file timestamps. Constructing <see cref="MainWindow"/> under the
/// headless platform runs the exact same code path (XAML populate + namescope
/// registration + choice-box wiring), so this class of build artifact rot can
/// never ship silently again.
/// </summary>
public sealed class MainWindowBootSmokeTests
{
    /// <summary>
    /// Every named element the settings pages wire up in code-behind must be
    /// registered in the window namescope. The shipped-broken dll failed here
    /// for <c>RenderingBackendBox</c> specifically.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("CpuEngineBox")]
    [InlineData("RenderingBackendBox")]
    [InlineData("LogLevelBox")]
    [InlineData("RenderResolutionBox")]
    [InlineData("WindowModeBox")]
    [InlineData("ScalingModeBox")]
    [InlineData("HdrModeBox")]
    [InlineData("LanguageBox")]
    [InlineData("DisplayBox")]
    [InlineData("ResolutionBox")]
    [InlineData("RefreshRateBox")]
    [InlineData("DefaultProfileBox")]
    public void MainWindow_RegistersAllNamedSettingsControls(string name)
    {
        var window = new MainWindow();
        try
        {
            var scope = window.FindNameScope();
            Assert.NotNull(scope);

            var control = scope.Find(name);
            Assert.True(
                control is not null,
                $"x:Name='{name}' was not registered in the MainWindow namescope. " +
                "The compiled XAML embedded in SharpEmu.GUI.dll is stale relative to " +
                "MainWindow.axaml — do a clean rebuild of SharpEmu.GUI before publishing.");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MainWindow_BootsAndWiresLocalizedChoiceBoxes()
    {
        // Constructing the window executes the exact startup sequence that
        // crashed for users: InitializeComponent (compiled-XAML populate +
        // namescope registration) followed by InitializeLocalizedChoiceBoxes.
        // Any unwired control throws NullReferenceException right here.
        var window = new MainWindow();
        try
        {
            var scope = window.FindNameScope();
            Assert.NotNull(scope);

            // Every localized choice combo must have its items assigned; a null
            // ItemsSource means InitializeLocalizedChoiceBoxes skipped or crashed
            // partway through.
            string[] choiceBoxes =
            [
                "CpuEngineBox",
                "RenderingBackendBox",
                "LogLevelBox",
                "RenderResolutionBox",
                "WindowModeBox",
                "ScalingModeBox",
                "HdrModeBox",
            ];

            foreach (var name in choiceBoxes)
            {
                var combo = Assert.IsAssignableFrom<ComboBox>(scope.Find(name));
                Assert.True(
                    combo.ItemsSource is not null,
                    $"choice box '{name}' has no ItemsSource; InitializeLocalizedChoiceBoxes did not run");
                Assert.True(
                    combo.ItemCount > 0,
                    $"choice box '{name}' has an empty ItemsSource");
            }

            // The launcher panel that hosts the rendering-backend row must exist
            // in the namescope, not just the AXAML text.
            Assert.NotNull(scope.Find("OptionsLauncherPanel"));
            Assert.NotNull(scope.Find("RenderingBackendRow"));
        }
        finally
        {
            window.Close();
        }
    }
}
