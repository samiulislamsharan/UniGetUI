using CommunityToolkit.WinUI.Notifications;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using UniGetUI.Core;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Widgets;
using UniGetUI.PackageEngine.Classes;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.Core.Logging;
using Windows.UI.Core;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.Core.Tools;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Interface
{

    public abstract partial class AbstractPackagesPage : Page
    {
        protected enum ReloadReason
        {
            FirstRun,
            Automated,
            Manual,
            External
        }

        protected enum FilterReason
        {
            FirstRun,
            PackagesChanged,
            FilterChanged,
        }

        protected OperationType PageRole = OperationType.Install;

        public DateTime LastPackageLoadTime { get; protected set; }

        public ObservableCollection<Package> Packages = new();
        public SortableObservableCollection<Package> FilteredPackages = new() { SortingSelector = (a) => (a.Name) };
        protected List<PackageManager> UsedManagers = new();
        protected Dictionary<PackageManager, List<ManagerSource>> UsedSourcesForManager = new();
        protected Dictionary<PackageManager, TreeViewNode> RootNodeForManager = new();
        protected Dictionary<ManagerSource, TreeViewNode> NodesForSources = new();

        public InfoBadge? ExternalCountBadge;

        protected bool Initialized = false;
        private bool AllSelected = true;


        int lastSavedWidth = 0;

        protected const string PAGE_NAME = "UNDEFINED";
        public string InstantSearchSettingString { get { return $"DisableInstantSearch{PAGE_NAME}Tab"; } }
        public string SidepalWidthSettingString { get { return $"SidepanelWidth{PAGE_NAME}Page"; } }

        public bool RoleIsUpdateLike { get { return PageRole == OperationType.Update; } }



        protected abstract Task<Package[]> LoadPackagesFromManager(PackageManager manager);
        protected abstract Task<bool> IsPackageValid(Package package);
        protected abstract void WhenAddingPackage(Package package);
        protected abstract Task WhenPackagesLoaded(ReloadReason reason);
        protected abstract void WhenPackageCountUpdated();
        protected abstract void WhenShowingContextMenu(Package package);
        public abstract void GenerateToolBar();
        public abstract BetterMenu GenerateContextMenu();
        public abstract void GenerateUIText();

        protected string NoPackages_BackgroundText = CoreTools.Translate("Hooray! No updates were found.");
        protected string NoPackages_SourcesText = CoreTools.Translate("Everything is up to date");
        protected string NoPackages_SubtitleText
        {
            get
            {
                return CoreTools.Translate("Everything is up to date") + " " +
                         CoreTools.Translate("(Last checked: {0})").Replace("{0}", LastPackageLoadTime.ToString());
            }
        }

        protected string NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria.");
        protected string NoMatches_SourcesText = CoreTools.Translate("No packages were found");
        protected string NoMatches_SubtitleText
        {
            get
            {
                return CoreTools.Translate("{0} packages were found, {1} of which match the specified filters.")
                                .Replace("{0}", Packages.Count.ToString()).Replace("{1}", (FilteredPackages.Count()).ToString()) + " " +
                    CoreTools.Translate("(Last checked: {0})").Replace("{0}", LastPackageLoadTime.ToString());
            }
        }
        protected string FoundPackages_SubtitleText { get { return NoMatches_SubtitleText; } }
        protected string MainTitleText = CoreTools.AutoTranslated("Software Updates");
        protected string MainTitleGlyph = "\uE895";



        public AbstractPackagesPage()
        {
            InitializeComponent();
            LastPackageLoadTime = DateTime.Now;
            QueryBothRadio.IsChecked = true;
            QueryOptionsGroup.SelectedIndex = 2;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            Initialized = true;
            ReloadButton.Click += async (s, e) => { await LoadPackages(); };
            FindButton.Click += (s, e) => { FilterPackages(QueryBlock.Text); };
            QueryBlock.TextChanged += (s, e) => { if (InstantSearchCheckbox.IsChecked == true) FilterPackages(QueryBlock.Text); };
            QueryBlock.KeyUp += (s, e) => { if (e.Key == Windows.System.VirtualKey.Enter) FilterPackages(QueryBlock.Text); };

            SourcesTreeView.Tapped += (s, e) =>
            {
                if (e.OriginalSource != null && (e.OriginalSource as FrameworkElement).DataContext != null)
                {
                    if ((e.OriginalSource as FrameworkElement).DataContext is TreeViewNode)
                    {
                        TreeViewNode node = (e.OriginalSource as FrameworkElement).DataContext as TreeViewNode;
                        if (node == null)
                            return;
                        if (SourcesTreeView.SelectedNodes.Contains(node))
                            SourcesTreeView.SelectedNodes.Remove(node);
                        else
                            SourcesTreeView.SelectedNodes.Add(node);
                        FilterPackages(QueryBlock.Text.Trim());
                    }
                }
            };
            PackageList.DoubleTapped += (s, e) =>
            {
                _ = MainApp.Instance.MainWindow.NavigationPage.ShowPackageDetails(PackageList.SelectedItem as Package, OperationType.Update);
            };

            PackageList.RightTapped += (s, e) =>
            {
                if (e.OriginalSource is FrameworkElement element)
                {
                    try
                    {
                        if (element.DataContext != null && element.DataContext is Package package)
                        {
                            PackageList.SelectedItem = package;
                            WhenShowingContextMenu(package);
                            (PackageList.ContextFlyout as BetterMenu).ShowAt(PackageList, e.GetPosition(PackageList));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex);
                    }
                }
            };

            PackageList.KeyUp += async (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && PackageList.SelectedItem != null)
                {
                    if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
                    {
                        if (await MainApp.Instance.MainWindow.NavigationPage.ShowInstallationSettingsForPackageAndContinue(PackageList.SelectedItem as Package, OperationType.Update))
                            MainApp.Instance.AddOperationToList(new UninstallPackageOperation(PackageList.SelectedItem as Package));
                    }
                    else if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
                        MainApp.Instance.AddOperationToList(new UpdatePackageOperation(PackageList.SelectedItem as Package));
                    else
                        _ = MainApp.Instance.MainWindow.NavigationPage.ShowPackageDetails(PackageList.SelectedItem as Package, OperationType.Update);
                }
                else if (e.Key == Windows.System.VirtualKey.A && InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
                {
                    if (AllSelected)
                        ClearItemSelection();
                    else
                        SelectAllItems();
                }
                else if (e.Key == Windows.System.VirtualKey.Space && PackageList.SelectedItem != null)
                {
                    (PackageList.SelectedItem as Package).IsChecked = !(PackageList.SelectedItem as Package).IsChecked;
                }
                else if (e.Key == Windows.System.VirtualKey.F5)
                {
                    _ = LoadPackages(ReloadReason.Manual);
                }
                else if (e.Key == Windows.System.VirtualKey.F1)
                {
                    MainApp.Instance.MainWindow.NavigationPage.ShowHelp();
                }
                else if (e.Key == Windows.System.VirtualKey.F && InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
                {
                    QueryBlock.Focus(FocusState.Programmatic);
                }
            };

            int width = 250;
            try
            {
                width = int.Parse(Settings.GetValue(SidepalWidthSettingString));
            }
            catch
            {
                Settings.SetValue(SidepalWidthSettingString, "250");
            }
            BodyGrid.ColumnDefinitions.ElementAt(0).Width = new GridLength(width);


            GenerateToolBar();
            LoadInterface();
            _ = LoadPackages(ReloadReason.Automated);

            QueryBlock.PlaceholderText = CoreTools.Translate("Search for packages");
        }

        protected void AddPackageToSourcesList(Package package)
        {
            if (!Initialized)
                return;

            ManagerSource source = package.Source;
            if (!UsedManagers.Contains(source.Manager))
            {
                UsedManagers.Add(source.Manager);
                TreeViewNode Node;
                Node = new TreeViewNode() { Content = source.Manager.Name + "                                                                                    .", IsExpanded = false };
                SourcesTreeView.RootNodes.Add(Node);
                SourcesTreeView.SelectedNodes.Add(Node);
                RootNodeForManager.Add(source.Manager, Node);
                UsedSourcesForManager.Add(source.Manager, new List<ManagerSource>());
                SourcesPlaceholderText.Visibility = Visibility.Collapsed;
                SourcesTreeViewGrid.Visibility = Visibility.Visible;
            }

            if ((!UsedSourcesForManager.ContainsKey(source.Manager) || !UsedSourcesForManager[source.Manager].Contains(source)) && source.Manager.Capabilities.SupportsCustomSources)
            {
                UsedSourcesForManager[source.Manager].Add(source);
                TreeViewNode item = new() { Content = source.Name + "                                                                                    ." };
                NodesForSources.Add(source, item);
                RootNodeForManager[source.Manager].Children.Add(item);

            }
        }

        private void PackageContextMenu_AboutToShow(object sender, Package _package)
        {
            UpgradablePackage package = _package as UpgradablePackage;
            if (!Initialized)
                return;
            PackageList.SelectedItem = package;
        }

        private void FilterOptionsChanged(object sender, RoutedEventArgs e)
        {
            if (!Initialized)
                return;
            FilterPackages(QueryBlock.Text);
        }

        private void InstantSearchValueChanged(object sender, RoutedEventArgs e)
        {
            if (!Initialized)
                return;
            Settings.Set(InstantSearchSettingString, InstantSearchCheckbox.IsChecked == false);
        }
        private void SourcesTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
        {
            FilterPackages(QueryBlock.Text);
        }

        /*
         * 
         * 
         *  DO NOT MODIFY THE UPPER PART OF THIS FILE
         * 
         * 
         */

        public async Task LoadPackages()
        {
            await LoadPackages(ReloadReason.External);
        }



        protected async Task LoadPackages(ReloadReason reason)
        {
            if (!Initialized)
                return;

            if (LoadingProgressBar.Visibility == Visibility.Visible)
                return; // If already loading, don't load again

            MainSubtitle.Text = CoreTools.Translate("Loading...");
            BackgroundText.Text = CoreTools.AutoTranslated("Loading...");
            BackgroundText.Visibility = Visibility.Visible;
            LoadingProgressBar.Visibility = Visibility.Visible;
            SourcesPlaceholderText.Visibility = Visibility.Visible;
            SourcesPlaceholderText.Text = CoreTools.AutoTranslated("Loading...");
            SourcesTreeViewGrid.Visibility = Visibility.Collapsed;

            Packages.Clear();
            FilteredPackages.Clear();
            UsedManagers.Clear();
            SourcesTreeView.RootNodes.Clear();
            UsedSourcesForManager.Clear();
            RootNodeForManager.Clear();
            NodesForSources.Clear();

            await Task.Delay(100);

            List<Task<Package[]>> tasks = new();

            foreach (PackageManager manager in MainApp.Instance.PackageManagerList)
            {
                if (manager.IsEnabled() && manager.Status.Found)
                {
                    Task<Package[]> task = LoadPackagesFromManager(manager);
                    tasks.Add(task);
                }
            }

            while (tasks.Count > 0)
            {
                foreach (Task<Package[]> task in tasks.ToArray())
                {
                    if (!task.IsCompleted)
                        await Task.Delay(100);

                    if (task.IsCompleted)
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            int InitialCount = Packages.Count;
                            foreach (Package package in task.Result)
                            {
                                if (!await IsPackageValid(package))
                                    continue;

                                Packages.Add(package);
                                WhenAddingPackage(package);
                                AddPackageToSourcesList(package);
                            }
                            if (InitialCount < Packages.Count)
                                FilterPackages(QueryBlock.Text.Trim(), StillLoading: true);
                        }
                        tasks.Remove(task);
                    }
                }
            }

            FilterPackages(QueryBlock.Text);
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            LastPackageLoadTime = DateTime.Now;
            await WhenPackagesLoaded(reason);
        }

        public void FilterPackages(string query, bool StillLoading = false)
        {
            if (!Initialized)
                return;

            FilteredPackages.Clear();

            List<ManagerSource> VisibleSources = new();
            List<PackageManager> VisibleManagers = new();

            if (SourcesTreeView.SelectedNodes.Count > 0)
            {
                foreach (TreeViewNode node in SourcesTreeView.SelectedNodes)
                {
                    if (NodesForSources.ContainsValue(node))
                        VisibleSources.Add(NodesForSources.First(x => x.Value == node).Key);
                    else if (RootNodeForManager.ContainsValue(node))
                        VisibleManagers.Add(RootNodeForManager.First(x => x.Value == node).Key);
                }
            }

            Package[] MatchingList;

            Func<string, string> CaseFunc;
            if (UpperLowerCaseCheckbox.IsChecked == true)
                CaseFunc = (x) => { return x; };
            else
                CaseFunc = (x) => { return x.ToLower(); };

            Func<string, string> CharsFunc;
            if (IgnoreSpecialCharsCheckbox.IsChecked == true)
                CharsFunc = (x) =>
                {
                    string temp_x = CaseFunc(x).Replace("-", "").Replace("_", "").Replace(" ", "").Replace("@", "").Replace("\t", "").Replace(".", "").Replace(",", "").Replace(":", "");
                    foreach (KeyValuePair<char, string> entry in new Dictionary<char, string>
                        {
                            {'a', "àáäâ"},
                            {'e', "èéëê"},
                            {'i', "ìíïî"},
                            {'o', "òóöô"},
                            {'u', "ùúüû"},
                            {'y', "ýÿ"},
                            {'c', "ç"},
                            {'ñ', "n"},
                        })
                    {
                        foreach (char InvalidChar in entry.Value)
                            x = x.Replace(InvalidChar, entry.Key);
                    }
                    return temp_x;
                };
            else
                CharsFunc = (x) => { return CaseFunc(x); };

            if (QueryIdRadio.IsChecked == true)
                MatchingList = Packages.Where(x => CharsFunc(x.Name).Contains(CharsFunc(query))).ToArray();
            else if (QueryNameRadio.IsChecked == true)
                MatchingList = Packages.Where(x => CharsFunc(x.Id).Contains(CharsFunc(query))).ToArray();
            else if (QueryBothRadio.IsChecked == true)
                MatchingList = Packages.Where(x => CharsFunc(x.Name).Contains(CharsFunc(query)) | CharsFunc(x.Id).Contains(CharsFunc(query))).ToArray();
            else if (QueryExactMatch.IsChecked == true)
                MatchingList = Packages.Where(x => CharsFunc(x.Name) == CharsFunc(query) | CharsFunc(x.Id) == CharsFunc(query)).ToArray();
            else // QuerySimilarResultsRadio == true
                MatchingList = Packages.ToArray();

            FilteredPackages.BlockSorting = true;
            foreach (Package match in MatchingList)
            {
                if (VisibleManagers.Contains(match.Manager) || VisibleSources.Contains(match.Source))
                    FilteredPackages.Add(match);
            }
            FilteredPackages.BlockSorting = false;
            FilteredPackages.Sort();
            UpdatePackageCount(StillLoading);
        }

        

        public void UpdatePackageCount(bool StillLoading = false)
        {
            if (FilteredPackages.Count() == 0)
            {
                if (!StillLoading)
                {
                    if (Packages.Count() == 0)
                    {
                        BackgroundText.Text = NoPackages_BackgroundText;
                        SourcesPlaceholderText.Text = NoPackages_SourcesText;
                        MainSubtitle.Text = NoPackages_SubtitleText;
                    }
                    else
                    {
                        BackgroundText.Text = NoMatches_BackgroundText;
                        SourcesPlaceholderText.Text = NoMatches_SourcesText;
                        MainSubtitle.Text = NoMatches_SubtitleText;
                    }
                    BackgroundText.Visibility = Visibility.Visible;
                }

            }
            else
            {
                BackgroundText.Visibility = Visibility.Collapsed;
                MainSubtitle.Text = FoundPackages_SubtitleText;
            }

            if (ExternalCountBadge != null)
            {
                ExternalCountBadge.Visibility = Packages.Count() == 0 ? Visibility.Collapsed : Visibility.Visible;
                ExternalCountBadge.Value = Packages.Count();
            }
            WhenPackageCountUpdated();
        }

        public void SortPackages(string Sorter)
        {
            if (!Initialized)
                return;

            FilteredPackages.Descending = !FilteredPackages.Descending;
            FilteredPackages.SortingSelector = (a) => (a.GetType().GetProperty(Sorter).GetValue(a));
            FilteredPackages.Sort();

            if (FilteredPackages.Count > 0)
                PackageList.ScrollIntoView(FilteredPackages[0]);
        }

        public void LoadInterface()
        {
            if (!Initialized)
                return;

            GenerateUIText();

            InstantSearchCheckbox.IsChecked = !Settings.Get(InstantSearchSettingString);

            MainTitle.Text = MainTitleText;
            HeaderIcon.Glyph = MainTitleGlyph;
            HeaderIcon.FontWeight = new Windows.UI.Text.FontWeight(700);
            CheckboxHeader.Content = " ";
            NameHeader.Content = CoreTools.Translate("Package Name");
            IdHeader.Content = CoreTools.Translate("Package ID");
            VersionHeader.Content = CoreTools.Translate("Version");
            NewVersionHeader.Content = CoreTools.Translate("New version");
            SourceHeader.Content = CoreTools.Translate("Source");

            CheckboxHeader.Click += (s, e) => { SortPackages("IsCheckedAsString"); };
            NameHeader.Click += (s, e) => { SortPackages("Name"); };
            IdHeader.Click += (s, e) => { SortPackages("Id"); };
            VersionHeader.Click += (s, e) => { SortPackages("VersionAsFloat"); };
            NewVersionHeader.Click += (s, e) => { SortPackages("NewVersionAsFloat"); };
            SourceHeader.Click += (s, e) => { SortPackages("SourceAsString"); };
            PackageList.ContextFlyout = GenerateContextMenu();
        }


        protected void SelectAllSourcesButton_Click(object sender, RoutedEventArgs e)
        {
            SourcesTreeView.SelectAll();
        }

        protected void ClearSourceSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            SourcesTreeView.SelectedItems.Clear();
            FilterPackages(QueryBlock.Text.Trim());
        }

        protected async void ShowDetailsForPackage(Package? package)
        {
            if (package == null)
                return;

            await MainApp.Instance.MainWindow.NavigationPage.ShowPackageDetails(package, PageRole);
        }

        protected void SharePackage(Package? package)
        {
            if(package == null)
                return;
            MainApp.Instance.MainWindow.SharePackage(package);
        }

        protected async void ShowInstallationOptionsForPackage(Package? package)
        {
            if(package == null)
                return;

            if (await MainApp.Instance.MainWindow.NavigationPage.ShowInstallationSettingsForPackageAndContinue(package, PageRole))
            {
                if(PageRole == OperationType.Install)
                    MainApp.Instance.AddOperationToList(new InstallPackageOperation(package));
                else if (PageRole == OperationType.Update)
                    MainApp.Instance.AddOperationToList(new UpdatePackageOperation(package));
                else // if (PageRole == OperationType.Uninstall)
                    MainApp.Instance.AddOperationToList(new UninstallPackageOperation(package));
            }
        }

        protected void SelectAllItems()
        {
            foreach (UpgradablePackage package in FilteredPackages)
                package.IsChecked = true;
            AllSelected = true;
        }

        protected void ClearItemSelection()
        {
            foreach (UpgradablePackage package in FilteredPackages)
                package.IsChecked = false;
            AllSelected = false;
        }

        public void RemoveCorrespondingPackages(Package foreignPackage)
        {
            foreach (UpgradablePackage package in Packages.ToArray())
                if (package.Equals(foreignPackage))
                {
                    Packages.Remove(package);
                    package.Tag = PackageTag.Default;
                    if (FilteredPackages.Contains(package))
                        FilteredPackages.Remove(package);
                }
            UpdatePackageCount();
        }

        private void SidepanelWidth_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width == ((int)(e.NewSize.Width / 10)) || e.NewSize.Width == 25)
                return;

            lastSavedWidth = ((int)(e.NewSize.Width / 10));
            Settings.SetValue("SidepanelWidthUpdatesPage", ((int)e.NewSize.Width).ToString());
            foreach (UIElement control in SidePanelGrid.Children)
            {
                control.Visibility = e.NewSize.Width > 20 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

    }
}
