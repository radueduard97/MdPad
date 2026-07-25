using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Storage.Pickers;
using Windows.UI;

namespace MdPad;

/// <summary>
/// The settings pane: six pages down the left, live-applied rows down the right, and no
/// OK button — every change takes effect on the window behind the dialog, which is the
/// only way to judge a font or a theme. The file it writes is plain JSON and is meant to
/// be edited by hand too, so nothing here is the only way to reach a setting.
/// </summary>
public static class SettingsDialog
{
    private static bool _open;

    public static async Task ShowAsync(XamlRoot root)
    {
        if (_open)
        {
            return;  // Ctrl+, while the pane is up should not stack a second copy.
        }

        _open = true;
        try
        {
            bool again;
            do
            {
                again = await ShowOnceAsync(root);
            }
            while (again);
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>Returns true when the pane should be rebuilt — after a reset or a reload.</summary>
    private static async Task<bool> ShowOnceAsync(XamlRoot root)
    {
        // Fill most of the window, but never demand more room than the display has:
        // the pane has to stay usable on a laptop screen as well as a desktop one.
        double shellWidth = Math.Clamp(root.Size.Width - 120, 520, 920);
        double shellHeight = Math.Clamp(root.Size.Height - 200, 360, 640);

        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 4, 20, 12),
        };

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 176,
            Content = body,
        };

        var pages = new (string Tag, string Glyph, Func<FrameworkElement> Build)[]
        {
            ("Appearance", "", BuildAppearance),
            ("Fonts", "", BuildFonts),
            ("Editor", "", BuildEditor),
            ("Skills", "", BuildSkills),
            ("Files", "", BuildFiles),
            ("Find", "", BuildFind),
        };

        foreach ((string tag, string glyph, _) in pages)
        {
            nav.MenuItems.Add(new NavigationViewItem
            {
                Content = tag,
                Tag = tag,
                Icon = new FontIcon { Glyph = glyph },
            });
        }

        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem { Tag: string tag }
                && pages.FirstOrDefault(p => p.Tag == tag) is { Build: not null } page)
            {
                body.Content = page.Build();
                body.ChangeView(null, 0, null, disableAnimation: true);
            }
        };
        nav.SelectedItem = nav.MenuItems[0];

        bool rebuild = false;

        var openFile = new HyperlinkButton { Content = "Open settings.json", Padding = new Thickness(0, 4, 12, 4) };
        openFile.Click += (_, _) => OpenSettingsFile();

        var reset = new HyperlinkButton { Content = "Reset to defaults", Padding = new Thickness(0, 4, 0, 4) };

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(openFile);
        footer.Children.Add(reset);

        var shell = new Grid { Width = shellWidth, Height = shellHeight };
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(nav, 0);
        Grid.SetRow(footer, 1);
        shell.Children.Add(nav);
        shell.Children.Add(footer);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Settings",
            Content = shell,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // ContentDialog's template caps its content at roughly 548x756 — sized for a
        // confirmation prompt, not for a settings pane. These two keys are the only way
        // past it, and without them the pages are clipped down to a column of stubs.
        dialog.Resources["ContentDialogMaxWidth"] = shellWidth + 80;
        dialog.Resources["ContentDialogMaxHeight"] = shellHeight + 160;
        dialog.Resources["ContentDialogMinWidth"] = shellWidth + 80;

        reset.Click += (_, _) =>
        {
            Settings.Reset();
            rebuild = true;
            dialog.Hide();
        };

        await dialog.ShowAsync();
        return rebuild;
    }

    private static void OpenSettingsFile()
    {
        try
        {
            Settings.Save();  // The file may not exist yet on a first run.
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
        }
    }

    // ---- Pages ----------------------------------------------------------------

    private static FrameworkElement BuildAppearance()
    {
        AppearanceSettings a = Settings.Current.Appearance;
        var page = NewPage();

        page.Children.Add(Section("Theme"));
        page.Children.Add(Card(
            "App theme",
            "Window, tabs, sidebar and menus.",
            Combo(a.Theme, new[]
            {
                (AppTheme.System, "Follow Windows"),
                (AppTheme.Light, "Light"),
                (AppTheme.Dark, "Dark"),
            }, v => a.Theme = v)));

        page.Children.Add(Card(
            "Preview theme",
            "Themed against the app on purpose — the fastest way to see how a README reads for someone in the other theme.",
            Combo(a.PreviewTheme, new[]
            {
                (PreviewTheme.FollowApp, "Same as the app"),
                (PreviewTheme.Light, "Always light"),
                (PreviewTheme.Dark, "Always dark"),
            }, v => a.PreviewTheme = v)));

        page.Children.Add(Card(
            "Window backdrop",
            "Solid is the one to pick for screen recording and remote sessions, where Mica reads as muddy.",
            Combo(a.Backdrop, new[]
            {
                (BackdropKind.Mica, "Mica"),
                (BackdropKind.MicaAlt, "Mica Alt"),
                (BackdropKind.Acrylic, "Acrylic"),
                (BackdropKind.Solid, "Solid"),
            }, v => a.Backdrop = v)));

        page.Children.Add(Section("Accent"));
        var swatch = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(ParseColor(a.AccentColor)),
        };

        var picker = new ColorPicker
        {
            Color = ParseColor(a.AccentColor),
            IsAlphaEnabled = false,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
        };
        picker.ColorChanged += (_, args) =>
        {
            a.AccentColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            swatch.Background = new SolidColorBrush(args.NewColor);
            Settings.Apply();
        };

        var pickerButton = new Button
        {
            Content = swatch,
            Padding = new Thickness(6),
            Flyout = new Flyout { Content = picker },
            IsEnabled = a.Accent == AccentSource.Custom,
        };

        var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        accentRow.Children.Add(Combo(a.Accent, new[]
        {
            (AccentSource.System, "Windows accent"),
            (AccentSource.Custom, "Custom"),
        }, v =>
        {
            a.Accent = v;
            pickerButton.IsEnabled = v == AccentSource.Custom;
        }));
        accentRow.Children.Add(pickerButton);

        page.Children.Add(Card("Links, quote bars and the skill label", "Everything the preview draws in the accent colour.", accentRow));

        page.Children.Add(Section("Layout"));
        page.Children.Add(Card(
            "Preview measure",
            "Widest the rendered text is allowed to get. 0 fills the pane; long lines at full width are hard to read.",
            Number(a.PreviewMaxWidth, 0, 4000, 20, v => a.PreviewMaxWidth = v)));

        return page;
    }

    private static FrameworkElement BuildFonts()
    {
        FontSettings f = Settings.Current.Fonts;
        var page = NewPage();

        page.Children.Add(Section("Editor"));
        page.Children.Add(Card("Family", "Monospace keeps tables and nested lists aligned while you write them.",
            FontBox(f.EditorFamily, Monospace, v => f.EditorFamily = v)));
        page.Children.Add(Card("Size", null, Number(f.EditorSize, 8, 48, 1, v => f.EditorSize = v)));

        page.Children.Add(Section("Preview"));
        page.Children.Add(Card("Family", "The proportional font a reader would see.",
            FontBox(f.PreviewFamily, Proportional, v => f.PreviewFamily = v)));
        page.Children.Add(Card("Size", "Headings and the skill card scale with this.",
            Number(f.PreviewSize, 8, 48, 1, v => f.PreviewSize = v)));
        page.Children.Add(Card("Line height", "Body text leading, in pixels. WinUI's TextBox has no equivalent, so this is preview-only.",
            Number(f.PreviewLineHeight, 0, 80, 1, v => f.PreviewLineHeight = v)));

        page.Children.Add(Section("Code"));
        page.Children.Add(Card("Family", "Code fences, inline code, the agent view and the reference list.",
            FontBox(f.CodeFamily, Monospace, v => f.CodeFamily = v)));
        page.Children.Add(Card("Size", "Usually a point or two under the editor.",
            Number(f.CodeSize, 8, 48, 1, v => f.CodeSize = v)));

        page.Children.Add(Section("Zoom"));
        page.Children.Add(Card("Zoom", "Multiplies every size above. Ctrl+= and Ctrl+- do the same; Ctrl+0 resets.",
            Number(Math.Round(f.Zoom * 100), 50, 300, 10, v => f.Zoom = v / 100.0, "%")));

        return page;
    }

    private static FrameworkElement BuildEditor()
    {
        EditorSettings e = Settings.Current.Editor;
        var page = NewPage();

        page.Children.Add(Section("Display"));
        page.Children.Add(Card("Word wrap", "Off gives you a horizontal scrollbar and accurate line numbers.",
            Toggle(e.WordWrap, v => e.WordWrap = v)));
        page.Children.Add(Card("Line numbers", "Counted in source lines, so the gutter stays hidden while word wrap is on.",
            Toggle(e.ShowLineNumbers, v => e.ShowLineNumbers = v)));
        page.Children.Add(Card("Spell check", "Windows' own checker, over the whole document — Markdown syntax included.",
            Toggle(e.SpellCheck, v => e.SpellCheck = v)));

        page.Children.Add(Section("Typing"));
        page.Children.Add(Card("Tab inserts spaces", "Tab indents instead of moving focus; a multi-line selection shifts as a block.",
            Toggle(e.TabInsertsSpaces, v => e.TabInsertsSpaces = v)));
        page.Children.Add(Card("Tab width", "Spaces per level. Two keeps nested lists inside YAML-adjacent files honest.",
            Number(e.TabWidth, 1, 8, 1, v => e.TabWidth = (int)v)));
        page.Children.Add(Card("Continue lists and quotes", "Enter carries the marker down; Enter on an empty item ends the list.",
            Toggle(e.ContinueLists, v => e.ContinueLists = v)));
        page.Children.Add(Card("Close brackets and backticks", "Typing ` ( [ { or \" inserts its partner. Asterisks are left alone — they collide with bullets.",
            Toggle(e.AutoCloseMarkers, v => e.AutoCloseMarkers = v)));

        page.Children.Add(Section("On save"));
        page.Children.Add(Card("Trim trailing whitespace", "Applied to the buffer as well, so the tab does not come back dirty.",
            Toggle(e.TrimTrailingWhitespace, v => e.TrimTrailingWhitespace = v)));
        page.Children.Add(Card("End with a newline", "What every diff tool expects.",
            Toggle(e.EnsureTrailingNewline, v => e.EnsureTrailingNewline = v)));
        page.Children.Add(Card("Line endings", "LF is the one to use for anything that lives in a repository.",
            Combo(e.LineEnding, new[]
            {
                (LineEndingKind.Lf, "LF (Unix)"),
                (LineEndingKind.Crlf, "CRLF (Windows)"),
            }, v => e.LineEnding = v)));

        return page;
    }

    private static FrameworkElement BuildSkills()
    {
        SkillSettings s = Settings.Current.Skills;
        var page = NewPage();

        page.Children.Add(Section("Location"));
        page.Children.Add(Card("Skills folder", "Where New skill starts. Left empty, MdPad looks for ~/.claude/skills.",
            FolderBox(s.SkillsFolder, v => s.SkillsFolder = v)));

        page.Children.Add(Section("Budget"));
        page.Children.Add(Card("Characters per token", "The estimate's divisor. ~4 is the usual approximation; measure your own content against a real tokeniser if you want to be exact.",
            Number(s.CharsPerToken, 1, 20, 0.1, v => s.CharsPerToken = v)));
        page.Children.Add(Card("Warn above", "On-invoke tokens past which the status bar meter turns amber.",
            Number(s.BudgetWarnTokens, 0, 200000, 500, v => s.BudgetWarnTokens = (int)v)));
        page.Children.Add(Card("Critical above", "And past which it turns red — the point at which a body wants extracting into references.",
            Number(s.BudgetErrorTokens, 0, 200000, 500, v => s.BudgetErrorTokens = (int)v)));

        page.Children.Add(Section("Scope"));
        page.Children.Add(Card("Treat as a skill", "Whether the skill panels apply to any file carrying name and description, or only to a file actually called SKILL.md.",
            Combo(s.Detection, new[]
            {
                (SkillDetection.SkillFileOrFrontMatter, "SKILL.md, or any front matter with name + description"),
                (SkillDetection.SkillFileOnly, "Only SKILL.md"),
            }, v => s.Detection = v, width: 260)));
        page.Children.Add(Card("Orphan detection", "Which unreferenced files the sidebar flags.",
            Combo(s.OrphanScope, new[]
            {
                (OrphanScope.MarkdownOnly, "Markdown files only"),
                (OrphanScope.AllFiles, "Every file in the folder"),
            }, v => s.OrphanScope = v, width: 240)));
        page.Children.Add(Card("Ignore", "Comma-separated. A bare name hides a file or folder anywhere under the skill; *.ext hides by extension.",
            PatternBox(s.IgnorePatterns)));

        page.Children.Add(Section("Description thresholds"));
        page.Children.Add(Card("Too short below", "Characters. Under this, there is not enough for a model to route on.",
            Number(s.ThinDescriptionLength, 0, 1024, 10, v => s.ThinDescriptionLength = (int)v)));
        page.Children.Add(Card("Too long above", "Characters. Over this, every prompt pays for text most of them will not use.",
            Number(s.BloatedDescriptionLength, 0, 1024, 10, v => s.BloatedDescriptionLength = (int)v)));

        page.Children.Add(Section("Validation rules"));
        page.Children.Add(new TextBlock
        {
            Text = "Rules the loader itself enforces are fixed. These are the house-style calls — demote or "
                + "switch off the ones you disagree with rather than learning to ignore the whole panel.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 8),
            Foreground = Resource("TextFillColorSecondaryBrush"),
        });

        foreach (SkillRule rule in SkillRules.Configurable)
        {
            page.Children.Add(Card(rule.Label, rule.Note, RuleCombo(rule)));
        }

        return page;
    }

    private static FrameworkElement BuildFiles()
    {
        FileSettings f = Settings.Current.Files;
        var page = NewPage();

        page.Children.Add(Section("Startup"));
        page.Children.Add(Card("On launch", "Session restore brings back unsaved work as well as paths.",
            Combo(f.Startup, new[]
            {
                (StartupMode.RestoreSession, "Reopen the last session"),
                (StartupMode.BlankTab, "Start with a blank tab"),
                (StartupMode.Ask, "Ask each time"),
            }, v => f.Startup = v, width: 240)));

        page.Children.Add(Section("Autosave"));
        page.Children.Add(Card("Autosave", "Only ever writes files that already have a path — an untitled tab is never saved behind your back.",
            Combo(f.Autosave, new[]
            {
                (AutosaveMode.Off, "Off"),
                (AutosaveMode.OnFocusLoss, "When the editor loses focus"),
                (AutosaveMode.Interval, "Every few seconds"),
            }, v => f.Autosave = v, width: 240)));
        page.Children.Add(Card("Interval", "Seconds between autosaves.",
            Number(f.AutosaveSeconds, 5, 3600, 5, v => f.AutosaveSeconds = (int)v)));

        page.Children.Add(Section("Changes on disk"));
        page.Children.Add(Card("When a file changes underneath a tab",
            "A skill rewritten by an agent while it sits open here is the normal case. A stale buffer silently undoes that work on the next save.",
            Combo(f.ExternalChange, new[]
            {
                (ExternalChangeMode.ReloadClean, "Reload unchanged tabs, ask about edited ones"),
                (ExternalChangeMode.AlwaysAsk, "Always ask"),
                (ExternalChangeMode.Ignore, "Ignore"),
            }, v => f.ExternalChange = v, width: 260)));

        return page;
    }

    private static FrameworkElement BuildFind()
    {
        FindSettings f = Settings.Current.Find;
        var page = NewPage();

        page.Children.Add(Section("Defaults"));
        page.Children.Add(Card("Scope on open", "Folder scope is the one that matters for a skill — a rename has to reach every reference.",
            Combo(f.DefaultScope, new[]
            {
                (SearchScope.File, "This file"),
                (SearchScope.Folder, "This folder"),
            }, v => f.DefaultScope = v)));
        page.Children.Add(Card("Remember match case", "Carried between sessions rather than reset each time the bar opens.",
            Toggle(f.RememberOptions, v => f.RememberOptions = v)));

        page.Children.Add(Section("Folder search"));
        page.Children.Add(Card("Exclude", "Comma-separated names, folders or *.ext patterns.",
            PatternBox(f.ExcludePatterns)));
        page.Children.Add(Card("Skip files larger than", "Kilobytes. A generated file that big is rarely what you are looking for.",
            Number(f.MaxFileSizeKb, 1, 102400, 128, v => f.MaxFileSizeKb = (int)v, "KB")));
        page.Children.Add(Card("Confirm Replace all above", "Matches. A folder-wide replace writes files that are not open straight to disk, and that write is not undoable from here.",
            Number(f.ConfirmReplaceAbove, 0, 1000, 1, v => f.ConfirmReplaceAbove = (int)v)));

        return page;
    }

    // ---- Row building ---------------------------------------------------------

    private static StackPanel NewPage() => new() { Spacing = 4 };

    private static FrameworkElement Section(string title) => new TextBlock
    {
        Text = title,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 16, 0, 6),
    };

    /// <summary>
    /// One settings row: label and explanation on the left, the control on the right.
    /// Hand-rolled rather than pulled from the Community Toolkit — one Border and a Grid
    /// is not worth a package reference in an app that ships self-contained.
    /// </summary>
    private static FrameworkElement Card(string title, string? subtitle, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (subtitle is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Resource("TextFillColorSecondaryBrush"),
            });
        }
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        return new Border
        {
            Background = Resource("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Resource("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid,
        };
    }

    private static ComboBox Combo<T>(T value, (T Value, string Label)[] options, Action<T> set, double width = 200)
        where T : struct, Enum
    {
        var combo = new ComboBox { MinWidth = width, SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value.Equals(value))) };
        foreach ((_, string label) in options)
        {
            combo.Items.Add(new ComboBoxItem { Content = label });
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Length)
            {
                set(options[combo.SelectedIndex].Value);
                Settings.Apply();
            }
        };
        return combo;
    }

    private static ToggleSwitch Toggle(bool value, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "On", OffContent = "Off" };
        toggle.Toggled += (_, _) =>
        {
            set(toggle.IsOn);
            Settings.Apply();
        };
        return toggle;
    }

    private static FrameworkElement Number(double value, double min, double max, double step, Action<double> set, string? unit = null)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 132,
        };

        box.ValueChanged += (_, args) =>
        {
            // Clearing the box reports NaN; put the old value back rather than clamping to zero.
            if (double.IsNaN(args.NewValue))
            {
                box.Value = args.OldValue;
                return;
            }
            set(Math.Clamp(args.NewValue, min, max));
            Settings.Apply();
        };

        if (unit is null)
        {
            return box;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = unit,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Resource("TextFillColorSecondaryBrush"),
        });
        return row;
    }

    private static ComboBox RuleCombo(SkillRule rule)
    {
        RuleSeverity current = Settings.Current.Skills.RuleSeverities.TryGetValue(rule.Id, out RuleSeverity stored)
            ? stored
            : RuleSeverity.Default;

        (RuleSeverity Value, string Label)[] options =
        {
            (RuleSeverity.Default, $"Default ({rule.Default.ToString().ToLowerInvariant()})"),
            (RuleSeverity.Error, "Error"),
            (RuleSeverity.Warning, "Warning"),
            (RuleSeverity.Suggestion, "Note"),
            (RuleSeverity.Off, "Off"),
        };

        return Combo(current, options, v =>
        {
            if (v == RuleSeverity.Default)
            {
                Settings.Current.Skills.RuleSeverities.Remove(rule.Id);
            }
            else
            {
                Settings.Current.Skills.RuleSeverities[rule.Id] = v;
            }
        }, width: 168);
    }

    /// <summary>
    /// An editable family list. WinUI cannot enumerate installed fonts without a Win2D or
    /// DirectWrite dependency, so the list is the families a Windows machine reliably has
    /// and the box stays typeable for anything else installed.
    /// </summary>
    private static ComboBox FontBox(string value, string[] suggestions, Action<string> set)
    {
        var combo = new ComboBox { IsEditable = true, MinWidth = 200, Text = value };
        foreach (string family in suggestions)
        {
            combo.Items.Add(new ComboBoxItem { Content = family });
        }

        void Commit(string family)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                set(family.Trim());
                Settings.Apply();
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Content: string family })
            {
                Commit(family);
            }
        };
        combo.TextSubmitted += (_, args) => Commit(args.Text);
        combo.LostFocus += (_, _) => Commit(combo.Text);
        return combo;
    }

    private static readonly string[] Monospace =
    {
        "Cascadia Code", "Cascadia Mono", "Consolas", "Courier New", "Lucida Console",
        "JetBrains Mono", "Fira Code", "IBM Plex Mono", "Source Code Pro",
    };

    private static readonly string[] Proportional =
    {
        "Segoe UI", "Segoe UI Variable", "Calibri", "Georgia", "Verdana",
        "Arial", "Times New Roman", "Sitka Text", "Cambria",
    };

    private static FrameworkElement FolderBox(string value, Action<string> set)
    {
        var box = new TextBox { Text = value, MinWidth = 260, PlaceholderText = "~/.claude/skills" };
        box.LostFocus += (_, _) =>
        {
            set(box.Text.Trim());
            Settings.Apply();
        };

        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 5, 10, 5) };
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                box.Text = folder.Path;
                set(folder.Path);
                Settings.Apply();
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(browse);
        return row;
    }

    /// <summary>A comma-separated view over one of the ignore lists, edited in place.</summary>
    private static FrameworkElement PatternBox(List<string> patterns)
    {
        var box = new TextBox { Text = string.Join(", ", patterns), MinWidth = 260, TextWrapping = TextWrapping.Wrap };
        box.LostFocus += (_, _) =>
        {
            patterns.Clear();
            patterns.AddRange(box.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Settings.Apply();
        };
        return box;
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static Color ParseColor(string? value)
    {
        string hex = (value ?? string.Empty).TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return Color.FromArgb(255, 0x4C, 0x8D, 0xFF);
    }
}
