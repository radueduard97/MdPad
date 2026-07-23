using System;
using System.Runtime.InteropServices;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MdPad;

/// <summary>
/// The application window. It owns the tab strip (which doubles as the title bar)
/// and hosts a Frame for page content. Add UI and logic to MainPage.xaml /
/// MainPage.xaml.cs instead of here; MainPage drives the tabs through <see cref="Tabs"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomDragRegion);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        SizeToWorkArea();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>
    /// Open centred at a size that actually fits the display. The default window is
    /// larger than a 1440p screen at 150% scaling, which pushes the status bar and the
    /// right edge of the preview off-screen.
    /// </summary>
    private void SizeToWorkArea()
    {
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        // WorkArea and MoveAndResize are both in physical pixels; the caps are in
        // DIPs so the window keeps the same apparent size at any display scale.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

        int width = (int)Math.Min(work.Width * 0.86, 1500 * scale);
        int height = (int)Math.Min(work.Height * 0.9, 950 * scale);

        AppWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>The document tab strip, driven by <see cref="MainPage"/>.</summary>
    public TabView Tabs => TabStrip;

    /// <summary>Update the OS window title (taskbar and Alt+Tab); the tab itself shows the file name.</summary>
    public void SetTitle(string title) => Title = title;
}
