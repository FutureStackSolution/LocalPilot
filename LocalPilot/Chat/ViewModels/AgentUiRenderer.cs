using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalPilot.Models;

namespace LocalPilot.Chat.ViewModels
{
    /// <summary>
    /// Renders/normalizes agent UI presentation details used by chat control.
    /// </summary>
    public sealed class AgentUiRenderer
    {
        public ToolCallDisplayInfo GetToolCallDisplayInfo(ToolCallRequest request)
        {
            var info = new ToolCallDisplayInfo
            {
                Label = request?.Name ?? "Tool",
                Icon = "\uE76C",
                Detail = null
            };

            if (request == null) return info;

            if (request.Name == "read_file")
            {
                info.Label = "Reading file";
                info.Icon = "\uE8A5"; // Document
                info.Detail = FileNameFromArg(request.Arguments, "path");
            }
            else if (request.Name == "grep_search")
            {
                info.Label = "Searching codebase";
                info.Icon = "\uE721"; // Search
            }
            else if (request.Name == "list_directory")
            {
                info.Label = "Exploring directory";
                info.Icon = "\uE8B7"; // Folder
            }
            else if (request.Name == "write_file" || request.Name == "replace_text" || request.Name == "write_to_file" || request.Name == "replace_file_content")
            {
                info.Label = "Updating code";
                info.Icon = "\uE74E"; // Save
                info.Detail = FileNameFromArg(request.Arguments, "TargetFile") ?? FileNameFromArg(request.Arguments, "path");
            }
            else if (request.Name == "run_terminal")
            {
                info.Label = "Running command";
                info.Icon = "\uE756"; // Terminal
            }
            else if (request.Name == "delete_file")
            {
                info.Label = "Deleting file";
                info.Icon = "\uE74D"; // Delete
                info.Detail = FileNameFromArg(request.Arguments, "path");
            }
            else if (request.Name == "rename_symbol")
            {
                info.Label = "Refactoring symbol";
                info.Icon = "\uE8AC"; // Rename
                info.Detail = StringArg(request.Arguments, "new_name");
            }
            else if (request.Name == "list_errors")
            {
                info.Label = "Checking for errors";
                info.Icon = "\uE117"; // Warning
            }
            else if (request.Name == "run_tests")
            {
                info.Label = "Running tests";
                info.Icon = "\uE9DB"; // Beaker/Lab
            }

            return info;
        }

        public Border CreateTerminalBadge(AgentStatusViewState statusState, ResourceDictionary resources, out Button acceptButton)
        {
            acceptButton = null;
            var badge = new Border
            {
                Background = resources["LpMenuBgBrush"] as Brush,
                BorderBrush = resources["LpMenuBorderBrush"] as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            sp.Children.Add(new TextBlock
            {
                Text = statusState.IsCancelled ? "\uE106" : (statusState.IsFailure ? "\uE10A" : "\uE73E"), // Cancel (Circle-Slash), Error (Warning), or Success (Tick)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = statusState.IsCancelled 
                    ? resources["LpMutedFgBrush"] as Brush 
                    : (statusState.IsFailure ? resources["LpStopBrush"] as Brush : resources["LpSuccessBrush"] as Brush ?? resources["LpAntigravityBlue"] as Brush)
            });
 
            sp.Children.Add(new TextBlock
            {
                Text = statusState.IsCancelled ? "Task cancelled." : (statusState.IsFailure ? "Task stopped due to an error." : "Task completed."),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = resources["LpWindowFgBrush"] as Brush,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (statusState.IsCompletion)
            {
                acceptButton = new Button
                {
                    Content = "Accept All",
                    Style = resources["LpPrimaryActionButtonStyle"] as Style,
                    Margin = new Thickness(16, -4, -4, -4), // Nested alignment
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 10,
                    Height = 24
                };
                sp.Children.Add(acceptButton);
            }

            badge.Child = sp;
            return badge;
        }

        public Border CreateWorkRow(string label, string icon, string detail, ResourceDictionary resources, Brush iconBrush = null)
        {
            var node = new Border { Style = resources["ActionNodeStyle"] as Style };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBlock = new TextBlock
            {
                Text = icon,
                Style = resources["ActionNodeIconStyle"] as Style,
                Foreground = iconBrush ?? resources["LpAccentBrush"] as Brush
            };
            
            // Force MDL2 assets for binary icons
            iconBlock.FontFamily = new FontFamily("Segoe MDL2 Assets");

            Grid.SetColumn(iconBlock, 0);
            row.Children.Add(iconBlock);

            var labelBlock = new TextBlock
            {
                Text = label,
                Style = resources["ActionNodeHeaderStyle"] as Style,
                FontSize = 12 // Match TerminalBadge size for consistency
            };
            Grid.SetColumn(labelBlock, 1);
            row.Children.Add(labelBlock);

            if (!string.IsNullOrEmpty(detail))
            {
                var detailBlock = new TextBlock
                {
                    Text = detail,
                    FontSize = 10,
                    Foreground = resources["LpMutedFgBrush"] as Brush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(detailBlock, 2);
                row.Children.Add(detailBlock);
            }

            node.Child = row;
            
            // 🚀 Professional Entrance: (Slide only, no transparency to avoid rendering issues)
            node.Opacity = 1.0;
            
            var slide = new DoubleAnimation(8, 0, new Duration(TimeSpan.FromSeconds(0.4)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            node.RenderTransform = new TranslateTransform();
            node.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);

            return node;
        }

        private static string StringArg(Dictionary<string, object> args, string key)
        {
            if (args == null) return null;
            if (!args.TryGetValue(key, out var val) || val == null) return null;
            return val.ToString();
        }

        private static string FileNameFromArg(Dictionary<string, object> args, string key)
        {
            var value = StringArg(args, key);
            if (string.IsNullOrWhiteSpace(value)) return null;

            try
            {
                return System.IO.Path.GetFileName(value) ?? value;
            }
            catch
            {
                return value;
            }
        }
    }

    public sealed class ToolCallDisplayInfo
    {
        public string Label { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Detail { get; set; }
    }
}
