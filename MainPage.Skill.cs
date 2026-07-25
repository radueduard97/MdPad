using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Storage.Pickers;

namespace MdPad;

/// <summary>
/// The New Skill command: ask for the two things that cannot be guessed — the name and
/// the description — and lay down a folder that already satisfies the contract the
/// sidebar checks against.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>Where the last skill was created, so a run of them does not mean re-picking the folder.</summary>
    private static string? _lastSkillParent;

    private async void OnNewSkill(object sender, RoutedEventArgs e) => await NewSkillAsync();

    private async Task NewSkillAsync()
    {
        string parent = _lastSkillParent ?? SkillScaffold.DefaultParentFolder();

        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "review-pull-request",
            FontFamily = new FontFamily("Consolas"),
        };

        var descriptionBox = new TextBox
        {
            Header = "Description",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
            PlaceholderText = "What it does, and when an agent should reach for it.",
        };

        var locationText = new TextBlock
        {
            Text = parent,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 4, 10, 4) };

        var location = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        location.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        location.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(locationText, 0);
        Grid.SetColumn(browse, 1);
        location.Children.Add(locationText);
        location.Children.Add(browse);

        var preview = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };

        var problemText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(nameBox);
        panel.Children.Add(descriptionBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Location",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(location);
        panel.Children.Add(preview);
        panel.Children.Add(problemText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "New skill",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        void Revalidate()
        {
            string name = nameBox.Text.Trim();
            string? problem = SkillScaffold.Problem(parent, name);

            problemText.Text = problem ?? string.Empty;
            problemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = problem is null;

            preview.Text = name.Length == 0
                ? string.Empty
                : $"{name}/\n  {SkillValidator.SkillFileName}\n  {SkillScaffold.ReferencesFolder}/{SkillScaffold.StarterReference}";
        }

        nameBox.TextChanged += (_, _) => Revalidate();
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow(picker);

            Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                parent = folder.Path;
                locationText.Text = parent;
                Revalidate();
            }
        };
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string skillName = nameBox.Text.Trim();
        string description = descriptionBox.Text.Trim();
        if (description.Length == 0)
        {
            description = SkillScaffold.DefaultDescription(skillName);
        }

        try
        {
            string path = await SkillScaffold.CreateAsync(parent, skillName, description);
            _lastSkillParent = parent;

            await OpenPathAsync(path);

            // A new skill is there to be written, not read: land in the split view with
            // the caret on the description, which is the line that decides everything.
            SetViewMode(ViewMode.Split);
            SelectFrontMatterDescription();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowMessageAsync("Could not create the skill", ex.Message);
        }
    }

    /// <summary>Put the caret on the description's value, ready to be typed over.</summary>
    private void SelectFrontMatterDescription()
    {
        if (FrontMatter.Field(FrontMatter.Parse(Editor.Text), "description") is not { } field)
        {
            return;
        }

        int lineStart = OffsetOfLine(Editor.Text, field.Line);
        int colon = Editor.Text.IndexOf(':', lineStart);
        if (colon < 0)
        {
            return;
        }

        int valueStart = colon + 1;
        while (valueStart < Editor.Text.Length && Editor.Text[valueStart] == ' ')
        {
            valueStart++;
        }

        int end = Editor.Text.IndexOfAny(new[] { '\r', '\n' }, valueStart);
        Editor.Focus(FocusState.Programmatic);
        Editor.Select(valueStart, (end < 0 ? Editor.Text.Length : end) - valueStart);
    }
}
