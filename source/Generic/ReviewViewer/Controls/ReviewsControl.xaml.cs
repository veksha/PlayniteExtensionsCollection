﻿using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using PluginsCommon;
using PluginsCommon.Web;
using ReviewViewer.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace ReviewViewer.Controls
{
    /// <summary>
    /// Interaction logic for ReviewsControl.xaml
    /// </summary>
    public partial class ReviewsControl : PluginUserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var caller = name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static readonly Regex steamLinkRegex = new Regex(@"^https?:\/\/store\.steampowered\.com\/app\/(\d+)", RegexOptions.Compiled);
        private readonly DesktopView ActiveViewAtCreation;
        IPlayniteAPI PlayniteApi;
        public ReviewViewerSettingsViewModel SettingsModel { get; }
        
        private string currentSteamId;
        private Game currentGame;

        public enum ReviewSearchType { All, Positive, Negative };

        private Visibility thumbsUpVisibility = Visibility.Collapsed;
        public Visibility ThumbsUpVisibility
        {
            get => thumbsUpVisibility;
            set
            {
                thumbsUpVisibility = value;
                OnPropertyChanged();
            }
        }

        private Visibility thumbsDownVisibility = Visibility.Collapsed;
        public Visibility ThumbsDownVisibility
        {
            get => thumbsDownVisibility;
            set
            {
                thumbsDownVisibility = value;
                OnPropertyChanged();
            }
        }

        private Visibility mainPanelVisibility = Visibility.Collapsed;
        public Visibility MainPanelVisibility
        {
            get => mainPanelVisibility;
            set
            {
                mainPanelVisibility = value;
                OnPropertyChanged();
            }
        }

        private ReviewSearchType selectedReviewSearch = ReviewSearchType.All;
        public ReviewSearchType SelectedReviewSearch
        {
            get => selectedReviewSearch;
            set
            {
                selectedReviewSearch = value;
                OnPropertyChanged();
            }
        }

        private ILogger logger = LogManager.GetLogger();
        private string pluginUserDataPath;

        string reviewsApiMask = @"https://store.steampowered.com/appreviews/{0}?json=1&purchase_type=all&language={1}&review_type={2}&playtime_filter_min=0&filter=summary";
        private string steamApiLanguage;

        private bool multipleReviewsAvailable = false;

        public Dictionary<string, string> ReviewTypesSource;

        private ReviewsResponse reviews;
        public ReviewsResponse Reviews
        {
            get => reviews;
            set
            {
                reviews = value;
                OnPropertyChanged();
            }
        }

        private string calculatedScore;
        public string CalculatedScore
        {
            get => calculatedScore;
            set
            {
                calculatedScore = value;
                OnPropertyChanged();
            }
        }

        private int selectedReviewDisplayIndex;
        public int SelectedReviewDisplayIndex
        {
            get => selectedReviewDisplayIndex;
            set
            {
                selectedReviewDisplayIndex = value;
                OnPropertyChanged();
            }
        }

        private string selectedReviewText;
        public string SelectedReviewText
        {
            get => selectedReviewText;
            set
            {
                selectedReviewText = value;
                OnPropertyChanged();
            }
        }

        private string formattedPlaytime;
        public string FormattedPlaytime
        {
            get => formattedPlaytime;
            set
            {
                formattedPlaytime = value;
                OnPropertyChanged();
            }
        }

        private string totalFormattedPlaytime;
        public string TotalFormattedPlaytime
        {
            get => totalFormattedPlaytime;
            set
            {
                totalFormattedPlaytime = value;
                OnPropertyChanged();
            }
        }

        private string reviewHelpfulnessHelpful;
        public string ReviewHelpfulnessHelpful
        {
            get => reviewHelpfulnessHelpful;
            set
            {
                reviewHelpfulnessHelpful = value;
                OnPropertyChanged();
            }
        }

        private string reviewHelpfulnessFunny;
        public string ReviewHelpfulnessFunny
        {
            get => reviewHelpfulnessFunny;
            set
            {
                reviewHelpfulnessFunny = value;
                OnPropertyChanged();
            }
        }

        private int selectedReviewIndex;
        public int SelectedReviewIndex
        {
            get => selectedReviewIndex;
            set
            {
                selectedReviewIndex = value;
                OnPropertyChanged();
                SelectedReview = reviews.Reviews[selectedReviewIndex];
                SetReviewInfo();
            }
        }

        private long totalReviewsAvailable = 0;
        public long TotalReviewsAvailable
        {
            get => totalReviewsAvailable;
            set
            {
                totalReviewsAvailable = value;
                OnPropertyChanged();
            }
        }

        private void SetReviewInfo()
        {
            var time = TimeSpan.FromMinutes(SelectedReview.Author.PlaytimeAtReview);
            FormattedPlaytime = string.Format(ResourceProvider.GetString("LOCReview_Viewer_ReviewPlaytimeFormat"), Math.Floor(time.TotalHours), time.ToString("mm"));

            time = TimeSpan.FromMinutes(SelectedReview.Author.PlaytimeForever);
            TotalFormattedPlaytime = string.Format(ResourceProvider.GetString("LOCReview_Viewer_ReviewTotalPlaytimeFormat"), Math.Floor(time.TotalHours), time.ToString("mm"));

            ReviewHelpfulnessHelpful = string.Format(ResourceProvider.GetString("LOCReview_Viewer_ReviewHelpfulnessHelpfulFormat"), SelectedReview.VotesUp);
            ReviewHelpfulnessFunny = string.Format(ResourceProvider.GetString("LOCReview_Viewer_ReviewHelpfulnessFunnyFormat"), SelectedReview.VotesFunny);
        }

        private Review selectedReview;
        public Review SelectedReview
        {
            get => selectedReview;
            set
            {
                selectedReview = value;
                ThumbsUpVisibility = Visibility.Collapsed;
                ThumbsDownVisibility = Visibility.Collapsed;
                if (selectedReview != null)
                {
                    if (selectedReview.VotedUp)
                    {
                        ThumbsUpVisibility = Visibility.Visible;
                    }
                    else
                    {
                        ThumbsDownVisibility = Visibility.Visible;
                    }
                    SelectedReviewText = BbCodeProcessor.FormatBbCodeToHtml(SelectedReview.ReviewReview);
                }

                OnPropertyChanged();
            }
        }

        public RelayCommand<object> NextReviewCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                NextReview();
            }, (a) => multipleReviewsAvailable);
        }

        void NextReview()
        {
            if (selectedReviewIndex == (Reviews.QuerySummary.NumReviews - 1))
            {
                SelectedReviewIndex = 0;
            }
            else
            {
                SelectedReviewIndex = selectedReviewIndex + 1;
            }
            SelectedReviewDisplayIndex = SelectedReviewIndex + 1;
        }

        public RelayCommand<object> PreviousReviewCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                PreviousReview();
            }, (a) => multipleReviewsAvailable);
        }

        void PreviousReview()
        {
            if (selectedReviewIndex == 0)
            {
                SelectedReviewIndex = Convert.ToInt32(Reviews.QuerySummary.NumReviews - 1);
            }
            else
            {
                SelectedReviewIndex = selectedReviewIndex - 1;
            }
            SelectedReviewDisplayIndex = SelectedReviewIndex + 1;
        }

        public RelayCommand<object> SwitchAllReviewsCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                SwitchAllReviews();
            }, (a) => selectedReviewSearch != ReviewSearchType.All);
        }

        void SwitchAllReviews()
        {
            selectedReviewSearch = ReviewSearchType.All;
            if (mainPanelVisibility == Visibility.Visible)
            {
                SummaryGrid.Visibility = Visibility.Visible;
            }
            UpdateReviewsContext();
        }

        public RelayCommand<object> SwitchPositiveReviewsCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                SwitchPositiveReviews();
            }, (a) => selectedReviewSearch != ReviewSearchType.Positive);
        }

        void SwitchPositiveReviews()
        {
            selectedReviewSearch = ReviewSearchType.Positive;
            SummaryGrid.Visibility = Visibility.Collapsed;
            Task.Run(() => UpdateReviewsContext());
        }

        public RelayCommand<object> SwitchNegativeReviewsCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                SwitchNegativeReviews();
            }, (a) => selectedReviewSearch != ReviewSearchType.Negative);
        }

        void SwitchNegativeReviews()
        {
            selectedReviewSearch = ReviewSearchType.Negative;
            SummaryGrid.Visibility = Visibility.Collapsed;
            Task.Run(() => UpdateReviewsContext());
        }

        public ReviewsControl(string pluginUserDataPath, string steamApiLanguage, ReviewViewerSettingsViewModel settings, IPlayniteAPI playniteApi)
        {
            InitializeComponent();
            this.PlayniteApi = playniteApi;
            SettingsModel = settings;
            selectedReviewSearch = ReviewSearchType.All;
            this.pluginUserDataPath = pluginUserDataPath;
            this.steamApiLanguage = steamApiLanguage;
            DataContext = this;
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                ActiveViewAtCreation = PlayniteApi.MainView.ActiveDesktopView;
            }
        }

        private void ResetBindingValues()
        {
            ThumbsUpVisibility = Visibility.Collapsed;
            ThumbsDownVisibility = Visibility.Collapsed;
            MainPanelVisibility = Visibility.Collapsed;
            multipleReviewsAvailable = false;
            SelectedReviewDisplayIndex = 0;
            TotalReviewsAvailable = 0;
            FormattedPlaytime = string.Empty;
            TotalFormattedPlaytime = string.Empty;
            SelectedReviewText = string.Empty;
            ReviewHelpfulnessHelpful = string.Empty;
            ReviewHelpfulnessFunny = string.Empty;
            CalculatedScore = "-";
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            //The GameContextChanged method is rised even when the control
            //is not in the active view. To prevent unecessary processing we
            //can stop processing if the active view is not the same one was
            //the one during creation
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop &&
                ActiveViewAtCreation != PlayniteApi.MainView.ActiveDesktopView)
            {
                return;
            }

            currentSteamId = null;
            if (newContext == null)
            {
                ResetBindingValues();
                return;
            }

            currentGame = newContext;
            Task.Run(() => UpdateReviewsContext());
        }

        public void UpdateReviewsContext()
        {
            ResetBindingValues();

            string reviewSearchType;
            switch (selectedReviewSearch)
            {
                case ReviewSearchType.Positive:
                    reviewSearchType = "positive";
                    break;
                case ReviewSearchType.Negative:
                    reviewSearchType = "negative";
                    break;
                default:
                    reviewSearchType = "all";
                    break;
            }

            var gameDataPath = Path.Combine(pluginUserDataPath, $"{currentGame.Id}_{reviewSearchType}.json");
            if (!FileSystem.FileExists(gameDataPath))
            {
                if (!SettingsModel.Settings.DownloadDataOnGameSelection)
                {
                    return;
                }
                
                if (currentSteamId == null)
                {
                    if (currentGame.PluginId == Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
                    {
                        currentSteamId = currentGame.GameId;
                    }
                    else
                    {
                        GetSteamIdFromLinks(currentGame);
                        if (currentSteamId == null)
                        {
                            MainPanelVisibility = Visibility.Collapsed;
                            return;
                        }
                    }
                }

                var uri = string.Format(reviewsApiMask, currentSteamId, steamApiLanguage, reviewSearchType);
                HttpDownloader.DownloadJsonFileAsync(uri, gameDataPath).GetAwaiter().GetResult();
                if (!FileSystem.FileExists(gameDataPath))
                {
                    return;
                }
            }

            try
            {
                Reviews = Serialization.FromJsonFile<ReviewsResponse>(gameDataPath);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Error deserializing file {gameDataPath}. Error: {e.Message}.");
            }
            
            try
            {
                if (Reviews.QuerySummary.NumReviews == 0)
                {
                    return;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Error obtaining reviews number for file {gameDataPath}. Error: {e.Message}.");
            }

            if (Reviews.QuerySummary.NumReviews > 1)
            {
                multipleReviewsAvailable = true;
            }

            SelectedReviewIndex = 0;
            SelectedReviewDisplayIndex = SelectedReviewIndex + 1;
            TotalReviewsAvailable = Reviews.QuerySummary.NumReviews;
            CalculateUserScore();
            MainPanelVisibility = Visibility.Visible;
        }

        private void GetSteamIdFromLinks(Game game)
        {
            if (game.Links == null)
            {
                return;
            }

            foreach (Link gameLink in game.Links)
            {
                var linkMatch = steamLinkRegex.Match(gameLink.Url);
                if (linkMatch.Success)
                {
                    currentSteamId = linkMatch.Groups[1].Value;
                    return;
                }
            }
        }

        private void CalculateUserScore()
        {
            // From https://steamdb.info/blog/steamdb-rating/
            
            double totalReviews = Reviews.QuerySummary.TotalReviews;
            double totalPositiveReviews = Reviews.QuerySummary.TotalPositive;
            double reviewScore = totalPositiveReviews / totalReviews;
            var score = reviewScore - (reviewScore - 0.5) * Math.Pow(2, -Math.Log10(totalReviews + 1));
            CalculatedScore = string.Format("{0}{1}", Math.Round(score * 100, 2).ToString(), "%");
        }

    }
}
