using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MdPad;

/// <summary>
/// Extract to a reference: take the section the caret is in, write it to
/// <c>references/&lt;name&gt;.md</c>, and leave a link in its place.
///
/// The budget meter says when a body has grown too expensive to load on invoke. This is
/// the edit that answers it, so the dialog leads with the number it moves: what the body
/// costs now, and what it will cost once the section is gone.
/// </summary>
public sealed partial class MainPage : Page
{
    private async void OnExtractReference(object sender, RoutedEventArgs e) => await ExtractReferenceAsync();

    private async Task ExtractReferenceAsync()
    {
        if (_current.Path is null)
        {
            await ShowMessageAsync(
                "Save the file first",
                "A reference is written next to the document, so MdPad needs to know where the document lives.");
            return;
        }

        ExtractionSection? section = ReferenceExtractor.Section(Editor.Text, Editor.SelectionStart, Editor.SelectionLength);
        if (section is null)
        {
            await ShowMessageAsync(
                "Nothing to extract",
                "Put the caret inside a section — a heading and the text under it — or select the lines to move.");
            return;
        }

        string documentPath = _current.Path;
        string folder = ReferenceExtractor.ReferencesFolder(documentPath);

        var nameBox = new TextBox
        {
            Header = "File",
            Text = ReferenceExtractor.DefaultFileName(section, documentPath),
            FontFamily = new FontFamily("Consolas"),
        };

        var noteBox = new TextBox
        {
            Header = "When to read it (optional)",
            PlaceholderText = "the detail the link is worth following for",
        };

        var moving = new TextBlock
        {
            Text = $"“{section.Title}” — {section.LineCount} line(s), ~{SkillAnalyzer.Format(SkillAnalyzer.EstimateTokens(section.Text))} tokens",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var locationText = new TextBlock
        {
            Text = folder,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };

        var linkPreview = new TextBlock
        {
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };

        var savings = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        };

        var problemText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(moving);
        panel.Children.Add(nameBox);
        panel.Children.Add(locationText);
        panel.Children.Add(noteBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Left behind",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(linkPreview);
        panel.Children.Add(savings);
        panel.Children.Add(new TextBlock
        {
            Text = "Both files are written to disk, so the link is never left pointing at nothing.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        panel.Children.Add(problemText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Extract to a reference",
            Content = panel,
            PrimaryButtonText = "Extract",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ExtractionPlan? plan = null;

        void Revalidate()
        {
            string? problem = ReferenceExtractor.FileNameProblem(documentPath, nameBox.Text);
            problemText.Text = problem ?? string.Empty;
            problemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = problem is null;

            if (problem is not null)
            {
                plan = null;
                return;
            }

            plan = ReferenceExtractor.Plan(Editor.Text, section, documentPath, nameBox.Text, noteBox.Text);
            linkPreview.Text = plan.Replacement.Replace("\r", "\n");

            // Measured against the buffer rather than the last render, so the figure matches
            // what is on screen even mid-edit.
            savings.Text = $"On invoke: ~{SkillAnalyzer.Format(SkillAnalyzer.EstimateTokens(Editor.Text))} → " +
                           $"~{SkillAnalyzer.Format(plan.RemainingTokens)} · " +
                           $"~{SkillAnalyzer.Format(plan.MovedTokens)} moves to on demand";
        }

        nameBox.TextChanged += (_, _) => Revalidate();
        noteBox.TextChanged += (_, _) => Revalidate();
        Revalidate();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || plan is null)
        {
            return;
        }

        await ApplyExtractionAsync(plan);
    }

    /// <summary>
    /// Write the new file and rewrite the body. Both halves land together: a reference on
    /// disk that nothing links to is exactly the orphan the sidebar warns about, so the
    /// document is saved as part of the same gesture.
    /// </summary>
    private async Task ApplyExtractionAsync(ExtractionPlan plan)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.FilePath)!);
            await File.WriteAllTextAsync(plan.FilePath, WithLineEndings(plan.FileContent));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync("Could not write the reference", ex.Message);
            return;
        }

        ExtractionSection section = plan.Section;

        // Land the caret on the link that replaced the section — the one line the author
        // may still want to word differently.
        ReplaceSelection(
            section.Start,
            section.End - section.Start,
            plan.Replacement,
            section.Start,
            plan.Replacement.Length);

        await SaveAsync();
        RefreshWatchers();
        UpdateBudget();
    }
}
