﻿using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Models;
using SteamKit2;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SteamGameTransferUtility.ViewModels
{
    class TransferWindowViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var caller = name;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI PlayniteApi;
        private readonly string steamInstallationDirectory;
        private List<string> steamLibraries;
        private string targetLibraryPath;
        private bool deleteSourceGame = false;
        private bool restartSteamIfNeeded = false;
        public List<string> SteamLibraries
        {
            get { return steamLibraries; }
            set
            {
                steamLibraries = value;
                OnPropertyChanged();
            }
        }
        public string TargetLibraryPath
        {
            get {return targetLibraryPath; }
            set
            {
                targetLibraryPath = value;
                OnPropertyChanged();
            }
        }

        public bool DeleteSourceGame
        {
            get { return deleteSourceGame; }
            set
            {
                deleteSourceGame = value;
                OnPropertyChanged();
            }
        }

        public bool RestartSteamIfNeeded
        {
            get { return restartSteamIfNeeded; }
            set
            {
                restartSteamIfNeeded = value;
                OnPropertyChanged();
            }
        }

        public TransferWindowViewModel(IPlayniteAPI api)
        {
            PlayniteApi = api;
            steamInstallationDirectory = GetSteamInstallationPath();
            steamLibraries = GetLibraryFolders();
        }

        public RelayCommand<string> OpenLibraryCommand
        {
            get => new RelayCommand<string>((targetLibraryPath) =>
            {
                Process.Start(targetLibraryPath);
            });
        }

        public RelayCommand<string> TransferGamesCommand
        {
            get => new RelayCommand<string>((targetLibraryPath) =>
            {
                TransferSteamGames(targetLibraryPath);
            });
        }

        public string GetSteamInstallationPath()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key?.GetValueNames().Contains("SteamPath") == true)
                {
                    return key.GetValue("SteamPath")?.ToString().Replace('/', '\\') ?? "c:\\program files (x86)\\steam";
                }
            }

            return "c:\\program files (x86)\\steam";
        }

        internal static List<string> GetLibraryFolders(KeyValue foldersData)
        {
            var dbs = new List<string>();
            foreach (var child in foldersData.Children)
            {
                if (int.TryParse(child.Name, out int _))
                {
                    if (!string.IsNullOrEmpty(child.Value))
                    {
                        dbs.Add(child.Value);
                    }
                    else if (child.Children.HasItems())
                    {
                        var path = child.Children.FirstOrDefault(a => a.Name?.Equals("path", StringComparison.OrdinalIgnoreCase) == true);
                        if (!string.IsNullOrEmpty(path.Value))
                        {
                            dbs.Add(Path.Combine(path.Value, "steamapps"));
                        }
                    }
                }
            }

            return dbs;
        }

        private List<string> GetLibraryFolders()
        {
            var dbs = new List<string>();
            var configPath = Path.Combine(steamInstallationDirectory, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(configPath))
            {
                return dbs;
            }

            try
            {
                using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read))
                {
                    var kv = new KeyValue();
                    kv.ReadAsText(fs);
                    foreach (var dir in GetLibraryFolders(kv))
                    {
                        if (Directory.Exists(dir))
                        {
                            dbs.Add(dir);
                        }
                        else
                        {
                            logger.Warn($"Found external Steam directory, but path doesn't exists: {dir}");
                        }
                    }
                }
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to get additional Steam library folders.");
            }

            return dbs;
        }

        public void TransferSteamGames(string targetLibraryPath)
        {
            var gameDatabase = PlayniteApi.MainView.SelectedGames.Where(g => g.PluginId == BuiltinExtensions.GetIdFromExtension(BuiltinExtension.SteamLibrary)).Where(g => g.IsInstalled == true);
            if (gameDatabase.Count() == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_DialogMessageNoSteamGamesSel"), "Steam Game Transfer Utility");
                return;
            }

            FileInfo t = new FileInfo(targetLibraryPath);
            var targetDrive = Path.GetPathRoot(t.FullName).ToLower();

            var copiedGamesCount = 0;
            var skippedGamesCount = 0;
            var deletedSourceFilesCount = 0;
            foreach (Game game in gameDatabase)
            {
                logger.Info(string.Format("Processing game: \"{0}\"", game.Name));

                // Verify that source and target library are not in the same drive
                FileInfo s = new FileInfo(game.InstallDirectory);
                var sourceDrive = Path.GetPathRoot(s.FullName).ToLower();
                if (sourceDrive == targetDrive)
                {
                    var errorMessage = string.Format("Source and target library are the same drive: \"{0}\"", sourceDrive);
                    logger.Warn(errorMessage);
                    skippedGamesCount++;
                    continue;
                }

                // Get steam source library that contains game
                var sourceLibraryPath = steamLibraries.FirstOrDefault(x => x.StartsWith(sourceDrive, StringComparison.InvariantCultureIgnoreCase));
                if (sourceLibraryPath == null)
                {
                    var errorMessage = string.Format(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_SteamLibNotDetected"), game.Name, sourceDrive);
                    PlayniteApi.Dialogs.ShowErrorMessage(errorMessage, "Steam Game Transfer Utility");
                    logger.Warn(errorMessage);
                    skippedGamesCount++;
                    continue;
                }

                // Check if game source manifest exists
                var gameManifest = string.Format("appmanifest_{0}.acf", game.GameId);
                var sourceManifestPath = Path.Combine(sourceLibraryPath, gameManifest);
                if (!File.Exists(sourceManifestPath))
                {
                    var errorMessage = string.Format(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_ErrorMessageSourceManifestNotDetected"), game.Name, sourceManifestPath);
                    PlayniteApi.Dialogs.ShowErrorMessage(errorMessage, "Steam Game Transfer Utility");
                    logger.Warn(errorMessage);
                    skippedGamesCount++;
                    continue;
                }

                // Check if game source directory exists
                var sourceGameDirectoryPath = Path.Combine(sourceLibraryPath, "common", GetAcfAppSubItem(sourceManifestPath, "installdir"));
                if (!Directory.Exists(sourceGameDirectoryPath))
                {
                    var errorMessage = string.Format(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_ErrorMessageSourceDirectoryNotDetected"), game.Name, sourceGameDirectoryPath);
                    PlayniteApi.Dialogs.ShowErrorMessage(errorMessage, "Steam Game Transfer Utility");
                    logger.Warn(errorMessage);
                    skippedGamesCount++;
                    continue;
                }

                // Check if game manifest already exists in target library
                var targetManifestPath = System.IO.Path.Combine(targetLibraryPath, gameManifest);
                if (File.Exists(targetManifestPath))
                {
                    var sourceBuildId = int.Parse(GetAcfAppSubItem(sourceManifestPath, "buildid"));
                    var targetBuildId = int.Parse(GetAcfAppSubItem(targetManifestPath, "buildid"));

                    if (sourceBuildId <= targetBuildId)
                    {
                        var errorMessage = string.Format("Game: {0}. Equal or greater BuldId. Source BuildId {1} - Target BuildId {2}", game.Name, sourceBuildId.ToString(), targetBuildId.ToString());
                        logger.Info(errorMessage);
                        skippedGamesCount++;

                        if (deleteSourceGame == true)
                        {
                            Directory.Delete(sourceGameDirectoryPath, true);
                            File.Delete(sourceManifestPath);
                            logger.Info($"Deleted source files of game {game.Name} in {sourceGameDirectoryPath}");
                            deletedSourceFilesCount++;
                        }
                        continue;
                    }
                }

                //Calculate size of source game directory
                var sourceDirectorySize = CalculateSize(sourceGameDirectoryPath);
                var progRes = PlayniteApi.Dialogs.ActivateGlobalProgress((a) =>
                {
                    string targetGameDirectoryPath = Path.Combine(targetLibraryPath, "common", GetAcfAppSubItem(sourceManifestPath, "installdir"));
                    if (Directory.Exists(targetGameDirectoryPath))
                    {
                        Directory.Delete(targetGameDirectoryPath, true);
                        logger.Info(string.Format("Deleted directory: {0}", targetGameDirectoryPath));
                    }

                    DirectoryCopy(sourceGameDirectoryPath, targetGameDirectoryPath, true);
                    logger.Info(string.Format("Game copied: {0}. sourceDirName: {1}, destDirName: {2}", game.Name, sourceGameDirectoryPath, targetGameDirectoryPath));
                    File.Copy(sourceManifestPath, targetManifestPath, true);
                    logger.Info(string.Format("Game manifest copied: {0}. sourceDirName: {1}, destDirName: {2}", game.Name, sourceManifestPath, targetManifestPath));
                    copiedGamesCount++;

                    if (deleteSourceGame == true)
                    {
                        Directory.Delete(sourceGameDirectoryPath, true);
                        File.Delete(sourceManifestPath);
                        logger.Info("Deleted source files");
                        deletedSourceFilesCount++;
                    }

                }, new GlobalProgressOptions(string.Format(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_ProgressDialogProcessingGame"), game.Name, sourceDirectorySize)));
            }

            var results = string.Format(ResourceProvider.GetString("LOCSteam_Game_Transfer_Utility_ResultsDialogMessage"), copiedGamesCount.ToString(), skippedGamesCount.ToString(), deletedSourceFilesCount.ToString());
            PlayniteApi.Dialogs.ShowMessage(results, "Steam Game Transfer Utility");

            if (restartSteamIfNeeded == true && (copiedGamesCount > 0 || deletedSourceFilesCount > 0))
            {
                RestartSteam();
            }
        }

        public string GetAcfAppSubItem(string acfPath, string subItemName)
        {
            AcfReader acfReader = new AcfReader(acfPath);
            ACF_Struct acfStruct = acfReader.ACFFileToStruct();
            return acfStruct.SubACF["AppState"].SubItems[subItemName];
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();

            // If the destination directory doesn't exist, create it.       
            Directory.CreateDirectory(destDirName);

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string tempPath = System.IO.Path.Combine(destDirName, file.Name);
                file.CopyTo(tempPath, false);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string tempPath = System.IO.Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
                }
            }
        }

        public string CalculateSize(string directory)
        {
            var task = Task.Run(() =>
            {
                return CalculateSizeTask(new DirectoryInfo(directory));
            });

            bool isCompletedSuccessfully = task.Wait(TimeSpan.FromMilliseconds(9000));

            if (isCompletedSuccessfully)
            {
                if (task.Result == 0)
                {
                    return "Unknown Size";
                }
                else
                {
                    return FormatBytes(task.Result);
                }
            }
            else
            {
                return "Unknown Size";
            }
        }

        private static long CalculateSizeTask(DirectoryInfo dir)
        {
            try
            {
                long size = 0;
                // Add file sizes.
                FileInfo[] fis = dir.GetFiles();
                foreach (FileInfo fi in fis)
                {
                    size += fi.Length;
                }

                // Add subdirectory sizes.
                DirectoryInfo[] dis = dir.GetDirectories();
                foreach (DirectoryInfo di in dis)
                {
                    size += CalculateSizeTask(di);
                }
                return size;
            }
            catch
            {
                return 0;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblSByte = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblSByte = bytes / 1024.0;
            }

            return string.Format("{0:0.##} {1}", dblSByte, Suffix[i]);
        }

        private async void RestartSteam()
        {
            string steamInstallationPath = System.IO.Path.Combine(GetSteamInstallationPath(), "steam.exe");
            if (!File.Exists(steamInstallationPath))
            {
                logger.Error(string.Format("Steam executable not detected in path \"{0}\"", steamInstallationPath));
                return;
            }
            bool isSteamRunning = GetIsSteamRunning();
            if (isSteamRunning == true)
            {
                Process.Start(steamInstallationPath, "-shutdown");
                logger.Info("Steam detected running. Closing via command line.");
                for (int i = 0; i < 8; i++)
                {
                    isSteamRunning = GetIsSteamRunning();
                    await PutTaskDelay(2000);
                    if (isSteamRunning == true)
                    {
                        logger.Info("Steam detected running.");
                    }
                    else
                    {
                        logger.Info("Steam has closed.");
                        break;
                    }
                }
            }
            if (isSteamRunning == false)
            {
                Process.Start(steamInstallationPath);
                logger.Info("Steam started.");
            }
        }

        async Task PutTaskDelay(int delayTime)
        {
            await Task.Delay(delayTime);
        }

        public bool GetIsSteamRunning()
        {
            Process[] processes = Process.GetProcessesByName("Steam");
            if (processes.Length > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

    }
}