using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalPilot.Chat.ViewModels
{
    /// <summary>
    /// Builds the staged-modifications panel UI and wires user actions via callbacks.
    /// </summary>
    public sealed class StagingPanelBuilder
    {
        public FrameworkElement Build(
            Dictionary<string, string> changes,
            ResourceDictionary resources,
            Func<string, string, Task> writeFileAsync,
            Func<string, string, Task> showDiffAsync,
            Action<string> appendMessage)
        {
            var border = new Border { 
                Style = resources["DeltaCardStyle"] as Style,
                HorizontalAlignment = HorizontalAlignment.Stretch 
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

            var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = "STAGED CHANGES",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = resources["LpMutedFgBrush"] as Brush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            title.Children.Add(new TextBlock
            {
                Text = "\uE70F",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = resources["LpAccentBrush"] as Brush,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            title.Children.Add(new TextBlock
            {
                Text = $"{changes.Count} Modifications Staged",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = resources["LpWindowFgBrush"] as Brush,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            var globalActions = new StackPanel { Orientation = Orientation.Horizontal };
            var btnAcceptAll = CreateGhostButton("Accept All", "\uE73E", resources["LpAccentBrush"] as Brush);
            btnAcceptAll.Margin = new Thickness(0, 0, 8, 0);
            btnAcceptAll.Click += (s, e) =>
            {
                _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    if (writeFileAsync != null)
                    {
                        foreach (var kvp in changes) await writeFileAsync(kvp.Key, kvp.Value);
                    }
                    await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    border.Visibility = Visibility.Collapsed;
                    appendMessage?.Invoke("✅ Changes integrated.");
                });
            };

            var btnRejectAll = CreateGhostButton(null, "\uE711", resources["LpMutedFgBrush"] as Brush);
            btnRejectAll.Click += (s, e) =>
            {
                border.Visibility = Visibility.Collapsed;
                appendMessage?.Invoke("❌ Changes discarded.");
            };

            globalActions.Children.Add(btnAcceptAll);
            globalActions.Children.Add(btnRejectAll);
            Grid.SetColumn(globalActions, 1);
            header.Children.Add(globalActions);
            stack.Children.Add(header);

            foreach (var kvp in changes)
            {
                var card = new Border
                {
                    Background = resources["LpGlassBgBrush"] as Brush,
                    CornerRadius = new CornerRadius(10), // Softer corners
                    Padding = new Thickness(16, 12, 16, 12), // More breathing room
                    Margin = new Thickness(0, 0, 0, 10),
                    BorderBrush = resources["LpGlassBorderBrush"] as Brush,
                    BorderThickness = new Thickness(1),
                    Opacity = 0.95 // Crisp but integrated
                };

                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var fileInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                fileInfo.Children.Add(new TextBlock
                {
                    Text = Path.GetFileName(kvp.Key),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = resources["LpWindowFgBrush"] as Brush
                });
                fileInfo.Children.Add(new TextBlock
                {
                    Text = kvp.Key,
                    FontSize = 10,
                    Foreground = resources["LpMutedFgBrush"] as Brush,
                    Opacity = 0.6,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(fileInfo, 0);
                row.Children.Add(fileInfo);

                var rowActions = new StackPanel { 
                    Orientation = Orientation.Horizontal, 
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right 
                };
                var btnDiff = CreateGhostButton(null, "\uE8A1", resources["LpMutedFgBrush"] as Brush);
                btnDiff.Click += (s, e) =>
                {
                    _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        if (showDiffAsync != null) await showDiffAsync(kvp.Key, kvp.Value);
                    });
                };

                var btnAccept = CreateGhostButton(null, "\uE73E", resources["LpAccentBrush"] as Brush);
                btnAccept.Click += (s, e) =>
                {
                    _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                    {
                        if (writeFileAsync != null) await writeFileAsync(kvp.Key, kvp.Value);
                        await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        card.Opacity = 0.3; // De-emphasize once integrated
                        card.IsEnabled = false;
                        btnAccept.Visibility = Visibility.Collapsed;
                        btnDiff.Visibility = Visibility.Collapsed;
                    });
                };

                rowActions.Children.Add(btnDiff);
                rowActions.Children.Add(btnAccept);
                Grid.SetColumn(rowActions, 1);
                row.Children.Add(rowActions);

                card.Child = row;
                stack.Children.Add(card);
            }

            border.Child = stack;
            return border;
        }

        private Button CreateGhostButton(string label, string icon, Brush fg)
        {
            var btn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(6, 4, 6, 4)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = fg,
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });

            btn.Content = sp;
            return btn;
        }
    }
}
