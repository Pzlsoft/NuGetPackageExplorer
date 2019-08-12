﻿using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGetPackageExplorer.Types;
using NuGetPe;
using PackageExplorerViewModel.PackageSearch;

namespace PackageExplorerViewModel
{
    public sealed class PackageChooserViewModel : ViewModelBase, IDisposable
    {
        private const int PackageListPageSize = 15;

        private IQueryContext<IPackageSearchMetadata>? _currentQuery;
        private string? _currentSearch;
        private FeedType _feedType;
        private MruPackageSourceManager? _packageSourceManager;
        private readonly IUIServices _uIServices;
        private readonly string? _defaultPackageSourceUrl;
        private bool _disposed;

        public PackageChooserViewModel(MruPackageSourceManager packageSourceManager,
                                       IUIServices uIServices,
                                       bool showPrereleasePackages,
                                       string? defaultPackageSourceUrl)
        {
            _showPrereleasePackages = showPrereleasePackages;
            _defaultPackageSourceUrl = defaultPackageSourceUrl;
            Packages = new ObservableCollection<PackageInfoViewModel>();

            SearchCommand = new RelayCommand<string>(Search, CanSearch);
            ClearSearchCommand = new RelayCommand(ClearSearch, CanClearSearch);
            LoadedCommand = new RelayCommand(async () => await LoadMore(CancellationToken.None));
            ChangePackageSourceCommand = new RelayCommand<string>(ChangePackageSource);
            CancelCommand = new RelayCommand(CancelCommandExecute, CanCancelCommandExecute);

            _packageSourceManager = packageSourceManager ?? throw new ArgumentNullException(nameof(packageSourceManager));
            _uIServices = uIServices;
        }

        #region Bound Properties

        private string? _currentTypingSearch;
        public string? CurrentTypingSearch
        {
            get { return _currentTypingSearch; }
            set
            {
                if (_currentTypingSearch != value)
                {
                    _currentTypingSearch = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _showPrereleasePackages;
        public bool ShowPrereleasePackages
        {
            get { return _showPrereleasePackages; }
            set
            {
                if (_showPrereleasePackages != value)
                {
                    _showPrereleasePackages = value;
                    OnPropertyChanged();

                    OnShowPrereleasePackagesChange();
                }
            }
        }

        public string PackageSource
        {
            get
            {
                CheckDisposed();
                return _defaultPackageSourceUrl ?? _packageSourceManager!.ActivePackageSource;
            }
            private set
            {
                if (_defaultPackageSourceUrl != null)
                {
                    throw new InvalidOperationException(
                        "Cannot set active package source when fixed package source is used.");
                }
                CheckDisposed();
                _packageSourceManager!.ActivePackageSource = value.Trim();
                OnPropertyChanged();
            }
        }

        public bool AllowsChangingPackageSource
        {
            get { return _defaultPackageSourceUrl == null; }
        }

        public ObservableCollection<string> PackageSources
        {
            get
            {
                CheckDisposed();
                return _packageSourceManager!.PackageSources;
            }
        }

        private bool _isEditable = true;
        public bool IsEditable
        {
            get { return _isEditable; }
            set
            {
                if (_isEditable != value)
                {
                    _isEditable = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<PackageInfoViewModel> Packages { get; private set; }

        private PackageInfoViewModel? _selectedPackageViewModel;
        public PackageInfoViewModel? SelectedPackageViewModel
        {
            get { return _selectedPackageViewModel; }
            set
            {
                if (_selectedPackageViewModel != value)
                {
                    if (_selectedPackageViewModel != null)
                    {
                        _selectedPackageViewModel.OnDeselected();
                    }

                    _selectedPackageViewModel = value;
                    OnPropertyChanged();
                }
            }
        }

        private string? _statusContent;
        public string? StatusContent
        {
            get { return _statusContent; }
            set
            {
                if (_statusContent != value)
                {
                    _statusContent = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _hasError;
        public bool HasError
        {
            get { return _hasError; }
            set
            {
                if (_hasError != value)
                {
                    _hasError = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        public SourceRepository? ActiveRepository { get; private set; }

        public PackageInfo? SelectedPackage
        {
            get
            {
                return _selectedPackageViewModel?.SelectedPackage;
            }
        }

        public bool HasMore => _currentQuery?.HasMore == true;

        private CancellationTokenSource? _currentCancellationTokenSource;
        private CancellationTokenSource? CurrentCancellationTokenSource
        {
            get { return _currentCancellationTokenSource; }
            set
            {
                _currentCancellationTokenSource = value;
                CancelCommand.RaiseCanExecuteChanged();
            }
        }

        public ICommand SearchCommand { get; private set; }
        public ICommand ClearSearchCommand { get; private set; }
        public ICommand LoadedCommand { get; private set; }
        public ICommand ChangePackageSourceCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }

        public event EventHandler LoadPackagesCompleted = delegate { };
        public event EventHandler OpenPackageRequested = delegate { };
        public event EventHandler PackageDownloadRequested = delegate { };

        private readonly PackageListCache<IPackageSearchMetadata> _packageListCache = new PackageListCache<IPackageSearchMetadata>();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling"),
         System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private async Task LoadPackages()
        {
            IsEditable = false;
            Packages.Clear();
            SelectedPackageViewModel = null;

            var usedTokenSource = new CancellationTokenSource();
            CurrentCancellationTokenSource = usedTokenSource;

            var repository = GetPackageRepository();

            _currentQuery = new ShowLatestVersionQueryContext<IPackageSearchMetadata>(repository, _currentSearch, ShowPrereleasePackages, PackageListPageSize, _packageListCache);
            _feedType = await repository.GetFeedType(usedTokenSource.Token);

            await LoadMore(usedTokenSource.Token);

            LoadPackagesCompleted(this, EventArgs.Empty);
        }

        private SourceRepository GetPackageRepository()
        {
            if (ActiveRepository == null)
            {
                _feedType = FeedType.Undefined;
                try
                {
                    ActiveRepository = PackageRepositoryFactory.CreateRepository(PackageSource);
                }
                catch (ArgumentException)
                {
                    var origSource = PackageSource;
                    PackageSource = _defaultPackageSourceUrl ?? NuGetConstants.DefaultFeedUrl;
                    ActiveRepository = PackageRepositoryFactory.CreateRepository(PackageSource);

                    _uIServices.Show($"Package Source '{origSource}' is not valid. Defaulting to '{NuGetConstants.DefaultFeedUrl}", MessageLevel.Error);
                }
            }

            return ActiveRepository;
        }

        private async Task LoadMore(CancellationToken token)
        {
            Debug.Assert(_currentQuery != null);

            IsEditable = false;

            var usedTokenSource = CurrentCancellationTokenSource;
            if (token == CancellationToken.None)
            {
                usedTokenSource = new CancellationTokenSource();
                token = usedTokenSource.Token;
                CurrentCancellationTokenSource = usedTokenSource;
            }

            try
            {
                var packageInfos = await _currentQuery.LoadMore(token);

                if (usedTokenSource != CurrentCancellationTokenSource)
                {
                    // This request has already been canceled. No need to process this request anymore.
                    return;
                }

                token.ThrowIfCancellationRequested();

                ClearMessage();

                var firstLoad = Packages.Count == 0;
                var repository = GetPackageRepository();
                Packages.AddRange(packageInfos.Select(p => new PackageInfoViewModel(p, ShowPrereleasePackages, repository, _feedType, this)));

                if (firstLoad)
                {
                    SelectedPackageViewModel = Packages?.FirstOrDefault();
                }
            }
            catch (OperationCanceledException)
            {
                if (usedTokenSource != CurrentCancellationTokenSource)
                {
                    // This request has already been canceled. No need to process this request anymore.
                    return;
                }

                ClearMessage();
            }
            catch (Exception exception)
            {
                if (usedTokenSource != CurrentCancellationTokenSource)
                {
                    // This request has already been canceled. No need to process this request anymore.
                    return;
                }

                DiagnosticsClient.TrackException(exception);

                var errorMessage = exception.Message;

                ShowMessage(errorMessage, true);
            }

            IsEditable = true;
            CurrentCancellationTokenSource = null;
        }

        #region Search
        private async void Search(string searchTerm)
        {
            searchTerm ??= CurrentTypingSearch ?? string.Empty;
            searchTerm = searchTerm.Trim();
            if (_currentSearch != searchTerm)
            {
                _currentSearch = searchTerm;
                await LoadPackages();
                CurrentTypingSearch = _currentSearch;
            }
        }

        private bool CanSearch(string searchTerm)
        {
            return IsEditable && !string.IsNullOrEmpty(searchTerm);
        }

        private async void ClearSearch()
        {
            CurrentTypingSearch = _currentSearch = string.Empty;
            await LoadPackages();
        }

        private bool CanClearSearch()
        {
            return IsEditable && !string.IsNullOrEmpty(_currentSearch);
        }
        #endregion

        private async void ChangePackageSource(string source)
        {
            if (PackageSource != source)
            {
                DiagnosticsClient.TrackEvent("PackageChooserViewModel_ChangePackageSource");

                CheckDisposed();


                PackageSource = source;

                ActiveRepository = null;
                try
                {
                    await LoadPackages();

                    // add the new source to MRU list, after the load succeeds, in case there's an error with the source
                    _packageSourceManager!.NotifyPackageSourceAdded(source);
                }
                catch (Exception e)
                {
                    _uIServices.Show(e.Message, MessageLevel.Error);
                }
            }
            else
            {
                try
                {
                    await LoadPackages();
                }
                catch (Exception e)
                {
                    _uIServices.Show(e.Message, MessageLevel.Error);
                }
            }
        }

        #region Status Bar
        private void ShowMessage(string message, bool isError)
        {
            StatusContent = message;
            HasError = isError;
        }

        private void ClearMessage()
        {
            ShowMessage(string.Empty, isError: false);
        }
        #endregion

        private async void OnShowPrereleasePackagesChange()
        {
            DiagnosticsClient.TrackEvent("PackageChooserViewModel_OnShowPrereleasePackagesChange");

            await LoadPackages();
        }

        public void OnAfterShow()
        {
            CurrentTypingSearch = _currentSearch;
        }

        internal void OnOpenPackage()
        {
            DiagnosticsClient.TrackEvent("PackageChooserViewModel_OnOpenPackage");
            OpenPackageRequested(this, EventArgs.Empty);
        }

        internal void OnDownloadPackage()
        {
            DiagnosticsClient.TrackEvent("PackageChooserViewModel_OnDownloadPackage");
            PackageDownloadRequested(this, EventArgs.Empty);
        }

        #region CancelCommand

        private void CancelCommandExecute()
        {
            if (CurrentCancellationTokenSource != null)
            {
                CurrentCancellationTokenSource.Cancel();
                ClearMessage();
                IsEditable = true;
            }
        }

        private bool CanCancelCommandExecute()
        {
            return !IsEditable && CurrentCancellationTokenSource != null;
        }

        #endregion

        public void Dispose()
        {
            if (_packageSourceManager != null)
            {
                _packageSourceManager.Dispose();
                _packageSourceManager = null;
            }

            _disposed = true;
            CurrentCancellationTokenSource?.Dispose();
        }

        private void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PackageChooserViewModel));
        }
    }
}
