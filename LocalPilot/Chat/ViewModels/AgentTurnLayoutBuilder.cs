using System;
using System.Windows;
using System.Windows.Controls;

namespace LocalPilot.Chat.ViewModels
{
    /// <summary>
    /// Builds top-level agent turn containers/bubbles for chat UI.
    /// </summary>
    public sealed class AgentTurnLayoutBuilder
    {
        public AgentTurnLayout BuildTurnLayout(Func<StackPanel> headerFactory, ResourceDictionary resources)
        {
            var turnContainer = new StackPanel { Margin = new Thickness(12, 8, 12, 20) };
            var currentContainer = turnContainer;

            if (headerFactory != null)
            {
                turnContainer.Children.Add(headerFactory());
            }

            var activityLabel = CreateSectionLabel("ACTIVITY", resources);
            activityLabel.Visibility = Visibility.Collapsed; // 👻 Hidden until activity starts
            turnContainer.Children.Add(activityLabel);
            
            var activityContainer = new ItemsControl
            {
                // 🚀 UI VIRTUALIZATION: Use VirtualizingStackPanel for high performance with 100+ logs
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            };

            // Enable virtualization properties
            VirtualizingStackPanel.SetIsVirtualizing(activityContainer, true);
            VirtualizingStackPanel.SetVirtualizationMode(activityContainer, VirtualizationMode.Recycling);

            var activityScroller = new ScrollViewer 
            { 
                Content = activityContainer, 
                MaxHeight = 220, 
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 10),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Visibility = Visibility.Collapsed
            };
            
            // Critical for VirtualizingStackPanel to work inside a ScrollViewer
            ScrollViewer.SetCanContentScroll(activityScroller, true);
            
            turnContainer.Children.Add(activityScroller);

            var narrativeLabel = CreateSectionLabel("RESPONSE", resources);
            narrativeLabel.Visibility = Visibility.Collapsed; // 👻 Hidden until content arrives
            turnContainer.Children.Add(narrativeLabel);
            
            var narrativeContainer = new StackPanel();
            turnContainer.Children.Add(narrativeContainer);

            return new AgentTurnLayout
            {
                TurnContainer = turnContainer,
                CurrentContainer = currentContainer,
                ActivityContainer = activityContainer,
                ActivityScroller = activityScroller,
                ActivityLabel = activityLabel,
                NarrativeContainer = narrativeContainer,
                NarrativeLabel = narrativeLabel
            };
        }

        public StackPanel EnsureAgentBubble(StackPanel currentContainer, Func<StackPanel> appendAIBubbleFactory)
        {
            if (currentContainer != null) return currentContainer;
            return appendAIBubbleFactory?.Invoke();
        }

        private static FrameworkElement CreateSectionLabel(string text, ResourceDictionary resources)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 8),
                Opacity = 1.0,
                Foreground = resources?["LpMutedFgBrush"] as System.Windows.Media.Brush,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
    }

    public sealed class AgentTurnLayout
    {
        public StackPanel TurnContainer { get; set; }
        public StackPanel CurrentContainer { get; set; }
        public ItemsControl ActivityContainer { get; set; }
        public ScrollViewer ActivityScroller { get; set; }
        public FrameworkElement ActivityLabel { get; set; }
        public StackPanel NarrativeContainer { get; set; }
        public FrameworkElement NarrativeLabel { get; set; }
    }
}
