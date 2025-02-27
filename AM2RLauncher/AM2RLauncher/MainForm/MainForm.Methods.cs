﻿using System;
using System.Collections.Generic;
using System.Text;
using Eto.Forms;
using System.Threading;
using LibGit2Sharp;
using System.IO;
using System.IO.Compression;
using Eto;
using System.Diagnostics;
using System.Linq;
using Eto.Drawing;
using System.Xml.Serialization;
using AM2RLauncher.XML;
using System.Net.NetworkInformation;
using System.Net;
using AM2RLauncher.Helpers;
using System.Text.RegularExpressions;

namespace AM2RLauncher
{
    partial class MainForm : Form
    {
        /// <summary>
        /// Checks if the repository has been validly cloned already.
        /// </summary>
        /// <returns><see langword="true"/> if yes, <see langword="false"/> if not.</returns>
        private bool IsPatchDataCloned()
        {
            //isValid seems to only check for a .git folder, and there are cases where that exists, but not the profile.xml
            return Repository.IsValid(CrossPlatformOperations.CURRENTPATH + "/PatchData") && File.Exists(CrossPlatformOperations.CURRENTPATH + "/PatchData/profile.xml");
        }

        /// <summary>
        /// Checks if AM2R 1.1 has been installed already, aka if a valid AM2R 1.1 Zip exists.
        /// </summary>
        /// <returns><see langword="true"/> if yes, <see langword="false"/> if not.</returns>
        private bool Is11Installed()
        {
            // return safely if file doesn't exist
            if (!File.Exists(CrossPlatformOperations.CURRENTPATH + "/AM2R_11.zip")) return false;
            var returnCode = CheckIfZipIsAM2R11(CrossPlatformOperations.CURRENTPATH + "/AM2R_11.zip");
            // Check if it's valid, if not log it, rename it and silently leave
            if (returnCode != IsZipAM2R11ReturnCodes.Successful)
            {
                log.Info("Detected invalid AM2R_11 zip with following error code: " + returnCode);
                HelperMethods.RecursiveRollover(CrossPlatformOperations.CURRENTPATH + "/AM2R_11.zip");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks if <paramref name="profile"/> is installed.
        /// </summary>
        /// <param name="profile">The <see cref="ProfileXML"/> that should be checked for installation.</param>
        /// <returns><see langword="true"/> if yes, <see langword="false"/> if not.</returns>
        private bool IsProfileInstalled(ProfileXML profile)
        {
            if (Platform.IsWinForms) return File.Exists(CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name + "/AM2R.exe");
            if (Platform.IsGtk) return File.Exists(CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name + "/AM2R.AppImage");   //should we check for .AppRun as well?
            return false;
        }

        /// <summary>
        /// Safety check function before accessing <see cref="profileIndex"/>.
        /// </summary>
        /// <returns><see langword="true"/> if it is valid, <see langword="false"/> if not.</returns>
        private bool IsProfileIndexValid()
        {
            return profileIndex != null;
        }

        /// <summary>
        /// Git Pulls from the repository.
        /// </summary>
        private void PullPatchData()
        {
            log.Info("Attempting to pull repository " + currentMirror + "...");
            using (var repo = new Repository(CrossPlatformOperations.CURRENTPATH + "/PatchData"))
            {
                // Permanently undo commits not pushed to remote
                Branch originMaster = repo.Branches["origin/master"];

                if (originMaster == null)
                {
                    // Directory exists, but seems corrupted, we delete it and prompt the user to download it again.
                    MessageBox.Show(Language.Text.CorruptPatchData, Language.Text.ErrorWindowTitle, MessageBoxType.Error);
                    HelperMethods.DeleteDirectory(CrossPlatformOperations.CURRENTPATH + "/PatchData");
                    throw new UserCancelledException();
                }

                repo.Reset(ResetMode.Hard, originMaster.Tip);

                // Credential information to fetch

                PullOptions options = new PullOptions();
                options.FetchOptions = new FetchOptions();
                options.FetchOptions.OnTransferProgress += TransferProgressHandlerMethod;

                // User information to create a merge commit
                var signature = new Signature("null", "null", DateTimeOffset.Now);

                // Pull
                try
                {
                    Commands.Pull(repo, signature, options);
                }
                catch
                {
                    log.Info("Repository pull attempt failed!");
                    return;
                }
            }
            log.Info("Repository pulled successfully.");
        }

        /// <summary>
        /// Deletes the given <paramref name="profile"/>. Reloads the <see cref="profileList"/> if <paramref name="reloadProfileList"/> is true.
        /// </summary>
        private void DeleteProfile(ProfileXML profile, bool reloadProfileList = true)
        {
            log.Info("Attempting to delete profile " + profile.Name + "...");

            // Delete folder in Mods
            if (Directory.Exists(CrossPlatformOperations.CURRENTPATH + profile.DataPath))
            {
                HelperMethods.DeleteDirectory(CrossPlatformOperations.CURRENTPATH + profile.DataPath);
            }

            // Delete the zip file in Mods
            if(File.Exists(CrossPlatformOperations.CURRENTPATH + profile.DataPath + ".zip"))
            {
                File.SetAttributes(CrossPlatformOperations.CURRENTPATH + profile.DataPath + ".zip", FileAttributes.Normal);     // for some reason, it was set at read only, so we undo that here
                File.Delete(CrossPlatformOperations.CURRENTPATH + profile.DataPath + ".zip");
            }

            // Delete folder in Profiles
            if (Directory.Exists(CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name))
            {
                HelperMethods.DeleteDirectory(CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name);
            }

            if (reloadProfileList)
                LoadProfiles();

            log.Info("Succesfully deleted profile " + profile.Name + ".");
        }

        /// <summary>
        /// Scans the PatchData and Mods folders for valid profile entries, and loads them.
        /// </summary>
        private void LoadProfiles()
        {
            log.Info("Loading profiles...");

            // Reset loaded profiles
            profileDropDown.Items.Clear();
            profileList.Clear();
            profileIndex = null;

            // Check for and add the Community Updates profile
            if (File.Exists(CrossPlatformOperations.CURRENTPATH + "/PatchData/profile.xml"))
            {
                profileList.Add(Serializer.Deserialize<ProfileXML>(File.ReadAllText(CrossPlatformOperations.CURRENTPATH + "/PatchData/profile.xml")));
                profileList[0].DataPath = "/PatchData/data";
            }

            // Safety check to generate the Mods folder if it does not exist
            if (!Directory.Exists(CrossPlatformOperations.CURRENTPATH + "/Mods"))
                Directory.CreateDirectory(CrossPlatformOperations.CURRENTPATH + "/Mods");

            // Get Mods folder info
            DirectoryInfo modsDir = new DirectoryInfo(CrossPlatformOperations.CURRENTPATH + "/Mods");

            // Add all extracted profiles in Mods to the profileList.
            foreach (DirectoryInfo dir in modsDir.GetDirectories())
            {
                foreach (FileInfo file in dir.GetFiles())
                {
                    if (file.Name == "profile.xml")
                    {
                        ProfileXML prof = Serializer.Deserialize<ProfileXML>(File.ReadAllText(dir.FullName + "/profile.xml"));
                        if (prof.Installable == true || IsProfileInstalled(prof)) // Safety check for non-installable profiles
                        {
                            prof.DataPath = "/Mods/" + dir.Name;
                            profileList.Add(prof);
                        }
                        else if (!IsProfileInstalled(prof)) // if not installable and isn't installed, remove it
                        {
                            prof.DataPath = "/Mods/" + dir.Name;
                            DeleteProfile(prof, false);
                        }
                    }
                }
            }

            // Add profile names to the profileDropDown
            foreach (ProfileXML profile in profileList)
            {
                // Archive version notes
                if (!profile.Installable)
                    profile.ProfileNotes = Language.Text.ArchiveNotes;

                profileDropDown.Items.Add(profile.Name);
            }

            // read the value from the config
            string profIndexString = CrossPlatformOperations.ReadFromConfig("ProfileIndex");

            //check if either no profile was found or the setting says that the last current profile didn't exist
            if (profileDropDown.Items.Count == 0)
                profileIndex = null;
            else
            {
                // we know that profiles exist at this point, so we're going to point it to 0 instead so the following code doesn't fail
                if (profIndexString == "null")
                    profIndexString = "0";

                // we parse from the settings, and check if profiles got deleted from the last time the launcher has been selected. if yes, we revert the last selection to 0;
                int intParseResult = Int32.Parse(profIndexString);
                profileIndex = intParseResult;
                if (profileIndex >= profileDropDown.Items.Count)
                    profileIndex = 0;
                profileDropDown.SelectedIndex = profileIndex.Value;
            }

            // Update stored profiles in the Profile Settings tab
            settingsProfileDropDown.Items.Clear();
            settingsProfileDropDown.Items.AddRange(profileDropDown.Items);
            settingsProfileDropDown.SelectedIndex = profileDropDown.Items.Count != 0 ? 0 : -1;

            log.Info("Profiles loaded.");

            // refresh the author and version label on the main tab
            if (profileList.Count > 0)
            {
                profileAuthorLabel.Text = Language.Text.Author + " " + profileList[profileDropDown.SelectedIndex].Author;
                profileVersionLabel.Text = Language.Text.VersionLabel + " " + profileList[profileDropDown.SelectedIndex].Version;
            }

            UpdateStateMachine();
        }

        /// <summary>
        /// Installs <paramref name="profile"/>.
        /// </summary>
        /// <param name="profile"><see cref="ProfileXML"/> to be installed.</param>
        private void InstallProfile(ProfileXML profile)
        {
            log.Info("Installing profile " + profile.Name + "...");

            // check if xdelta is installed on linux, by searching all folders in PATH
            if (Platform.IsGtk && !CrossPlatformOperations.CheckIfXdeltaIsInstalled())
            {
                Application.Instance.Invoke(new Action(() =>
                {
                    MessageBox.Show(Language.Text.XdeltaNotFound, Language.Text.WarningWindowTitle, MessageBoxButtons.OK);
                }));
                SetPlayButtonState(UpdateState.Install);
                UpdateStateMachine();
                log.Error("Xdelta not found. Aborting installing a profile...");
                return;
            }

            string profilesHomePath = CrossPlatformOperations.CURRENTPATH + "/Profiles";
            string profilePath = profilesHomePath + "/" + profile.Name;

            // Failsafe for Profiles directory
            if (!Directory.Exists(profilesHomePath))
                Directory.CreateDirectory(profilesHomePath);
            

            // This failsafe should NEVER get triggered, but Miepee's broken this too much for me to trust it otherwise.
            if (Directory.Exists(profilePath))
                Directory.Delete(profilePath, true);
            

            // Create profile directory
            Directory.CreateDirectory(profilePath);

            // Switch profilePath on Gtk
            if (Platform.IsGtk)
            {
                if (Directory.Exists(profilePath + "/assets"))
                    Directory.Delete(profilePath + "/assets", true);

                profilePath += "/assets";
                Directory.CreateDirectory(profilePath);
            }


            // Extract 1.1
            ZipFile.ExtractToDirectory(CrossPlatformOperations.CURRENTPATH + "/AM2R_11.zip", profilePath);

            // Extracted 1.1
            UpdateProgressBar(33);
            log.Info("Profile folder created and AM2R_11.zip extracted.");

            string dataPath;

            // Set local datapath for installation files
            dataPath = CrossPlatformOperations.CURRENTPATH + profile.DataPath;

            string datawin = null, exe = null;

            if(Platform.IsWinForms)
            {
                datawin = "data.win";
                exe = "AM2R.exe";
            }
            else if(Platform.IsGtk)
            {
                datawin = "game.unx";
                // use the exe name based on the desktop file in the appimage, rather than hardcoding it.
                string desktopContents = File.ReadAllText(CrossPlatformOperations.CURRENTPATH + "/PatchData/data/AM2R.AppDir/AM2R.desktop");
                exe = Regex.Match(desktopContents, @"(?<=Exec=).*").Value;
                log.Info("According to AppImage desktop file, use \"" + exe + "\" as game name.");
            }

            log.Info("Attempting to patch in " + profilePath);

            if (Platform.IsWinForms)
            {
                // Patch game executable
                if (profile.UsesYYC)
                {
                    CrossPlatformOperations.ApplyXdeltaPatch(profilePath + "/data.win", dataPath + "/AM2R.xdelta", profilePath + "/" + exe);
                    
                    // Delete 1.1's data.win, we don't need it anymore!
                    File.Delete(profilePath + "/data.win");
                }
                else
                {
                    CrossPlatformOperations.ApplyXdeltaPatch(profilePath + "/data.win", dataPath + "/data.xdelta", profilePath + "/" + datawin);
                    CrossPlatformOperations.ApplyXdeltaPatch(profilePath + "/AM2R.exe", dataPath + "/AM2R.xdelta", profilePath + "/" + exe);
                }
            }
            else if (Platform.IsGtk)    // YYC and VM look exactly the same on Linux so we're all good here.
            {
                CrossPlatformOperations.ApplyXdeltaPatch(profilePath + "/data.win", dataPath + "/game.xdelta", profilePath + "/" + datawin);
                CrossPlatformOperations.ApplyXdeltaPatch(profilePath + "/AM2R.exe", dataPath + "/AM2R.xdelta", profilePath + "/" + exe);
                Process.Start("chmod", "+x  \"" + profilePath + "/" + exe + "\"").WaitForExit();    //just in case the resulting file isn't chmoddded, we do it here.

                //these are not needed by linux at all, so we delete them
                File.Delete(profilePath + "/data.win");
                File.Delete(profilePath + "/AM2R.exe");
                File.Delete(profilePath + "/D3DX9_43.dll");

                // Move exe one directory out
                File.Move(profilePath + "/" + exe, profilePath.Substring(0, profilePath.LastIndexOf("/")) + "/" + exe);
            }

            // Applied patch
            if (Platform.IsWinForms) UpdateProgressBar(66);
            else if (Platform.IsGtk) UpdateProgressBar(44);//linux will take a bit longer, due to appimage creation
            log.Info("xdelta patch(es) applied.");

            // Install new datafiles
            HelperMethods.DirectoryCopy(dataPath + "/files_to_copy", profilePath);

            // HQ music
            if (!profile.UsesCustomMusic && (bool)hqMusicPCCheck.Checked)
                HelperMethods.DirectoryCopy(CrossPlatformOperations.CURRENTPATH + "/PatchData/data/HDR_HQ_in-game_music", profilePath);
            

            // Linux post-process
            if (Platform.IsGtk)
            {
                string assetsPath = profilePath;
                profilePath = profilePath.Substring(0, profilePath.LastIndexOf("/"));

                // Rename all songs to lowercase
                foreach (var file in new DirectoryInfo(assetsPath).GetFiles())
                    if(file.Name.EndsWith(".ogg") && !File.Exists(file.DirectoryName + "/" + file.Name.ToLower()))
                        File.Move(file.FullName, file.DirectoryName + "/" + file.Name.ToLower());

                //Copy AppImage template to here
                HelperMethods.DirectoryCopy(CrossPlatformOperations.CURRENTPATH + "/PatchData/data/AM2R.AppDir", profilePath + "/AM2R.AppDir/");

                // safety checks, in case the folders don't exist
                Directory.CreateDirectory(profilePath + "/AM2R.AppDir/usr/bin/");
                Directory.CreateDirectory(profilePath + "/AM2R.AppDir/usr/bin/assets/");

                // copy game assets to the appimageDir
                HelperMethods.DirectoryCopy(assetsPath, profilePath + "/AM2R.AppDir/usr/bin/assets/");
                File.Copy(profilePath + "/" + exe, profilePath + "/AM2R.AppDir/usr/bin/" + exe);

                UpdateProgressBar(66);
                log.Info("Gtk-specific formatting finished.");

                // temp save the currentWorkingDirectory and console.error, change it to profilePath and null, call the script, and change it back.
                string workingDir = Directory.GetCurrentDirectory();
                TextWriter cliError = Console.Error;
                Directory.SetCurrentDirectory(profilePath);
                Console.SetError(new StreamWriter(Stream.Null));
                Environment.SetEnvironmentVariable("ARCH", "x86_64");
                Process.Start(CrossPlatformOperations.CURRENTPATH + "/PatchData/utilities/appimagetool-x86_64.AppImage", "-n AM2R.AppDir").WaitForExit();
                Directory.SetCurrentDirectory(workingDir);
                Console.SetError(cliError);

                //Clean files
                Directory.Delete(profilePath + "/AM2R.AppDir", true);
                Directory.Delete(assetsPath, true);
                File.Delete(profilePath + "/" +exe);
                if (File.Exists(profilePath + "/AM2R.AppImage")) File.Delete(profilePath + "/AM2R.AppImage");
                File.Move(profilePath + "/" + "AM2R-x86_64.AppImage", profilePath + "/AM2R.AppImage");
            }

            // Copy profile.xml so we can grab data to compare for updates later!
            // tldr; check if we're in PatchData or not
            if(new DirectoryInfo(dataPath).Parent.Name == "PatchData")
                File.Copy(dataPath + "/../profile.xml", profilePath + "/profile.xml");
            else File.Copy(dataPath + "/profile.xml", profilePath + "/profile.xml");

            // Installed datafiles
            UpdateProgressBar(100);
            log.Info("Successfully installed profile " + profile.Name + ".");

            // This is just for visuals because the average windows end user will ask why it doesn't go to the end otherwise.
            if(Platform.IsWinForms)
                Thread.Sleep(1000);
        }

        /// <summary>
        /// Creates an APK of the selected <paramref name="profile"/>.
        /// </summary>
        /// <param name="profile"><see cref="ProfileXML"/> to be compiled into an APK.</param>
        private void CreateAPK(ProfileXML profile)
        {
            // Overall safety check just in case of bad situations
            if (profile.SupportsAndroid)
            {
                log.Info("Creating Android APK for profile " + profile.Name + ".");

                // Check for java, exit safely with a warning if not found!
                if (!CrossPlatformOperations.IsJavaInstalled())
                {
                    //message box show needs to be done on main thread
                    Application.Instance.Invoke(new Action(() =>
                    {
                        MessageBox.Show(Language.Text.JavaNotFound, Language.Text.WarningWindowTitle, MessageBoxButtons.OK);
                    }));
                    SetApkButtonState(ApkButtonState.Create);
                    UpdateStateMachine();
                    log.Error("Java not found! Aborting Android APK creation.");
                    return;
                }

                // check if xdelta is installed on linux, by searching all folders in PATH
                if (Platform.IsGtk && !CrossPlatformOperations.CheckIfXdeltaIsInstalled())
                {   
                    //message box show needs to be done on main thread
                    Application.Instance.Invoke(new Action(() =>
                    {
                        MessageBox.Show(Language.Text.XdeltaNotFound, Language.Text.WarningWindowTitle, MessageBoxButtons.OK);
                    }));
                    SetApkButtonState(ApkButtonState.Create);
                    UpdateStateMachine();
                    log.Error("Xdelta not found. Aborting Android APK creation...");
                    return;

                }

                log.Debug("java installed!");

                string proc = "",
                       args = "",
                       apktoolPath = CrossPlatformOperations.CURRENTPATH + "/PatchData/utilities/android/apktool.jar",
                       uberPath = CrossPlatformOperations.CURRENTPATH + "/PatchData/utilities/android/uber-apk-signer.jar";

                if (Platform.IsWinForms)
                {
                    proc = "cmd";
                    args = "/C java -jar ";
                }
                if (Platform.IsGtk)
                {
                    proc = "java";
                    args = "-jar ";
                }

                // Create working dir after some cleanup
                string tempDir = new DirectoryInfo(CrossPlatformOperations.CURRENTPATH + "/temp").FullName;

                string dataPath = CrossPlatformOperations.CURRENTPATH + profile.DataPath;

                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);

                Directory.CreateDirectory(tempDir);

                // Progress 1
                UpdateProgressBar(14);
                log.Info("Cleanup, variables, and working directory created.");

                // Extract AM2RWrapper.apk
                ProcessStartInfo apktoolStart = new ProcessStartInfo
                {
                    FileName = proc,
                    // for an explanation on the .replace look in CreateXdeltaPatch method
                    Arguments = args + "\"" + apktoolPath.Replace(CrossPlatformOperations.CURRENTPATH + "/","../") + "\" d \"" + dataPath.Replace(CrossPlatformOperations.CURRENTPATH + "/", "../") + "/android/AM2RWrapper.apk\"",
                    WorkingDirectory = tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process apktool = new Process
                {
                    StartInfo = apktoolStart
                };

                apktool.Start();

                apktool.WaitForExit();

                // Progress 2
                UpdateProgressBar(28);
                log.Info("AM2RWrapper decompiled.");

                // Add datafiles

                string workingDir = tempDir + "/AM2RWrapper/assets";

                // 1.1
                ZipFile.ExtractToDirectory(CrossPlatformOperations.CURRENTPATH + "/AM2R_11.zip", workingDir);

                // New datafiles
                HelperMethods.DirectoryCopy(dataPath + "/files_to_copy", workingDir);

                // HQ music
                if (hqMusicAndroidCheck.Checked == true)
                    HelperMethods.DirectoryCopy(CrossPlatformOperations.CURRENTPATH + "/PatchData/data/HDR_HQ_in-game_music", workingDir);

                // Add AM2R.ini
                // Yes, I'm aware this is dumb. If you've got any better ideas for how to copy a seemingly randomly named .ini from this folder to the APK, please let me know.
                foreach (FileInfo file in new DirectoryInfo(dataPath + "/android").GetFiles())
                {
                    if (file.Name.EndsWith(".ini"))
                    {
                        File.Copy(file.FullName, workingDir + "/" + file.Name);
                    }
                }

                // Progress 3
                UpdateProgressBar(42);
                log.Info("AM2R_11.zip extracted and datafiles copied into AM2RWrapper.");

                // Patch data.win to game.droid
                CrossPlatformOperations.ApplyXdeltaPatch(workingDir + "/data.win", dataPath + "/droid.xdelta", workingDir + "/game.droid");

                // Progress 4
                UpdateProgressBar(56);
                log.Info("game.droid successfully patched.");

                // Delete unnecessary files
                File.Delete(workingDir + "/AM2R.exe");
                File.Delete(workingDir + "/D3DX9_43.dll");
                File.Delete(workingDir + "/explanations.txt");
                File.Delete(workingDir + "/modifiers.ini");
                File.Delete(workingDir + "/readme.txt");
                File.Delete(workingDir + "/data.win");
                Directory.Delete(workingDir + "/mods", true);
                Directory.Delete(workingDir + "/lang/headers", true);

                if (Platform.IsGtk)
                {
                    File.Delete(workingDir + "/icon.png");
                }

                // Modify apktool.yml to NOT compress ogg files
                string apktoolText = File.ReadAllText(workingDir + "/../apktool.yml");
                apktoolText = apktoolText.Replace("doNotCompress:", "doNotCompress:\n- ogg");
                File.WriteAllText(workingDir + "/../apktool.yml", apktoolText);

                // Progress 5
                UpdateProgressBar(70);
                log.Info("Unnecessary files removed, apktool.yml modified to prevent ogg compression.");

                // Rebuild APK
                apktoolStart = new ProcessStartInfo
                {
                    FileName = proc,
                    Arguments = args + "\"" + apktoolPath.Replace(CrossPlatformOperations.CURRENTPATH + "/", "../") + "\" b AM2RWrapper -o \"" + profile.Name + ".apk\"",
                    WorkingDirectory = tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                apktool = new Process
                {
                    StartInfo = apktoolStart
                };

                apktool.Start();

                apktool.WaitForExit();

                // Progress 6
                UpdateProgressBar(84);
                log.Info("AM2RWrapper rebuilt into " + profile.Name + ".apk.");

                // Debug-sign APK
                ProcessStartInfo uberStart = new ProcessStartInfo
                {
                    FileName = proc,
                    Arguments = args + "\"" + uberPath.Replace(CrossPlatformOperations.CURRENTPATH + "/", "../") + "\" -a \"" + profile.Name + ".apk\"",
                    WorkingDirectory = tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process uber = new Process
                {
                    StartInfo = uberStart
                };

                uber.Start();

                uber.WaitForExit();

                // Extra file cleanup
                File.Copy(tempDir + "/" + profile.Name + "-aligned-debugSigned.apk", CrossPlatformOperations.CURRENTPATH + "/" + profile.Name + ".apk", true);

                // Progress 7
                UpdateProgressBar(100);
                log.Info(profile.Name + ".apk signed and moved to " + CrossPlatformOperations.CURRENTPATH + "/" + profile.Name + ".apk.");

                HelperMethods.DeleteDirectory(tempDir);

                CrossPlatformOperations.OpenFolderAndSelectFile(CrossPlatformOperations.CURRENTPATH + "/" + profile.Name + ".apk");

                log.Info("Successfully created Android APK for profile " + profile.Name + ".");
            }
        }

        /// <summary>
        /// Runs the Game, works cross platform.
        /// </summary>
        private void RunGame()
        {
            if (IsProfileIndexValid())
            {
                ProfileXML profile = profileList[profileIndex.Value];

                // These are used on both windows and linux for game logging
                string savePath = Platform.IsWinForms ? profile.SaveLocation.Replace("%localappdata%", Environment.GetEnvironmentVariable("LOCALAPPDATA")) : profile.SaveLocation.Replace("~", Environment.GetEnvironmentVariable("HOME"));
                DirectoryInfo logDir = new DirectoryInfo(savePath + "/logs");
                string date = string.Join("-", DateTime.Now.ToString().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

                log.Info("Launching game profile " + profile.Name + ".");

                if (Platform.IsWinForms)
                {
                    // sets the arguments to empty, or to the profiles save path/logs and create time based logs. Creates the folder if necessary.
                    string arguments = "";

                    // Game logging
                    if ((bool)profileDebugLogCheck.Checked)
                    {
                        log.Info("Performing logging setup for profile " + profile.Name + ".");

                        if (!Directory.Exists(logDir.FullName))
                            Directory.CreateDirectory(logDir.FullName);

                        if (File.Exists(logDir.FullName + "/" + profile.Name + ".txt"))
                            HelperMethods.RecursiveRollover(logDir.FullName + "/" + profile.Name + ".txt", 5);

                        StreamWriter stream = File.AppendText(logDir.FullName + "/" + profile.Name + ".txt");

                        stream.WriteLine("AM2RLauncher " + VERSION + " log generated at " + date);

                        stream.Flush();

                        stream.Close();

                        arguments = "-debugoutput \"" + logDir.FullName + "/" + profile.Name + ".txt\" -output \"" + logDir.FullName + "/" + profile.Name + ".txt\"";
                    }

                    ProcessStartInfo proc = new ProcessStartInfo();

                    proc.WorkingDirectory = CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name;
                    proc.FileName = proc.WorkingDirectory + "/AM2R.exe";
                    proc.Arguments = arguments;

                    log.Info("CWD of Profile is " + proc.WorkingDirectory);

                    using (var p = Process.Start(proc))
                    {
                        SetForegroundWindow(p.MainWindowHandle);
                        p.WaitForExit();
                    }
                }
                else if (Platform.IsGtk)
                {

                    ProcessStartInfo startInfo = new ProcessStartInfo();

                    log.Info("Is the environment textbox null or whitespace = " + string.IsNullOrWhiteSpace(customEnvVarTextBox.Text));

                    if(!string.IsNullOrWhiteSpace(customEnvVarTextBox.Text))
                    {
                        string envVars = customEnvVarTextBox.Text;

                        for(int i = 0; i < customEnvVarTextBox.Text.Count(f => f == '='); i++)
                        {
                            // env var variable
                            string variable = envVars.Substring(0, envVars.IndexOf('='));
                            envVars = envVars.Replace(variable + "=", "");

                            // this thing here is the value parser. Since values are sometimes "like this", i need to compensate for them.
                            int valueSubstringLength = 0;
                            if(envVars[0] != '"')               // if value is not embedded in "", check if there are spaces left. If yes, get the index of the space, if not that was the last
                            {
                                if (envVars.IndexOf(' ') >= 0)
                                    valueSubstringLength = envVars.IndexOf(' ')+1;
                                else
                                    valueSubstringLength = envVars.Length;
                            }
                            else                               // if value is embedded in "", check if there are spaces after the "". if yes, get index of that, if not that was the last
                            {
                                int secondQuoteIndex = envVars.IndexOf('"', envVars.IndexOf('"')+1);
                                if (envVars.IndexOf(' ', secondQuoteIndex) >= 0)
                                    valueSubstringLength = envVars.IndexOf(' ', secondQuoteIndex)+1;
                                else
                                    valueSubstringLength = envVars.Length;
                            }
                            // env var value
                            string value = envVars.Substring(0, valueSubstringLength);
                            envVars = envVars.Substring(value.Length);

                            log.Info("Adding variable \"" + variable + "\" with value \"" + value + "\"");
                            startInfo.EnvironmentVariables[variable] = value;
                        }
                    }

                    // IF we're supposed to log profiles, add events that track those and append them to this var. otherwise keep it null
                    string terminalOutput = null;

                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = CrossPlatformOperations.CURRENTPATH + "/Profiles/" + profile.Name;
                    startInfo.FileName = startInfo.WorkingDirectory + "/AM2R.AppImage";

                    log.Info("CWD of Profile is " + startInfo.WorkingDirectory);

                    log.Info("Launching game with following variables: ");
                    foreach(System.Collections.DictionaryEntry item in startInfo.EnvironmentVariables)
                    {
                        log.Info("Key: \"" + item.Key + "\" Value: \"" + item.Value + "\"");
                    }

                    using (Process p = new Process())
                    {
                        p.StartInfo = startInfo;
                        if ((bool)profileDebugLogCheck.Checked)
                        {
                            p.StartInfo.RedirectStandardOutput = true;
                            p.OutputDataReceived += new DataReceivedEventHandler((sender, e) => { terminalOutput += e.Data + "\n"; });

                            p.StartInfo.RedirectStandardError = true;
                            p.ErrorDataReceived += new DataReceivedEventHandler((sender, e) => { terminalOutput += e.Data + "\n"; });
                        }

                        p.Start();

                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();

                        p.WaitForExit();
                    }

                    if (terminalOutput != null)
                    {
                        log.Info("Performed logging setup for profile " + profile.Name + ".");

                        if (!Directory.Exists(logDir.FullName))
                            Directory.CreateDirectory(logDir.FullName);

                        if (File.Exists(logDir.FullName + "/" + profile.Name + ".txt"))
                            HelperMethods.RecursiveRollover(logDir.FullName + "/" + profile.Name + ".txt", 5);

                        StreamWriter stream = File.AppendText(logDir.FullName + "/" + profile.Name + ".txt");

                        // write general info
                        stream.WriteLine("AM2RLauncher " + VERSION + " log generated at " + date);

                        //write what was in the terminal
                        stream.WriteLine(terminalOutput);

                        stream.Flush();

                        stream.Close();
                    }

                }

                log.Info("Profile " + profile.Name + " process exited.");
            }
        }

        /// <summary>
        /// Method that updates <see cref="progressBar"/>.
        /// </summary>
        /// <param name="value">The value that <see cref="progressBar"/> should be set to.</param>
        /// <param name="min">The min value that <see cref="progressBar"/> should be set to.</param>
        /// <param name="max">The max value that <see cref="progressBar"/> should be set to.</param>
        private void UpdateProgressBar(int value, int min = 0, int max = 100)
        {
            Application.Instance.Invoke(new Action(() =>
            {
                progressBar.MinValue = min;
                progressBar.MaxValue = max;
                progressBar.Value = value;
            }));
        }

        /// <summary>
        /// Updates <see cref="updateState"/>, <see cref="playButton"/>, <see cref="apkButtonState"/>, and <see cref="apkButton"/> according to the current conditiions.
        /// </summary>
        private void UpdateStateMachine()
        {
            UpdatePlayState();
            UpdateApkState();
            UpdateProfileState();
            UpdateProfileSettingsState();
        }

        /// <summary>
        /// Determines current conditions and calls <see cref="SetPlayButtonState(UpdateState)"/> accordingly.
        /// </summary>
        private void UpdatePlayState()
        {
            // If not downloading or installing...
            if (updateState != UpdateState.Downloading && updateState != UpdateState.Installing)
            {
                if (apkButtonState != ApkButtonState.Creating)
                {
                    playButton.Enabled = true;
                    // If PatchData is cloned...
                    if (IsPatchDataCloned())
                    {
                        // If 1.1 is installed...
                        if (Is11Installed())
                        {
                            // If current profile is installed...
                            if (IsProfileIndexValid() && IsProfileInstalled(profileList[profileIndex.Value]))
                            {
                                // We're ready to play!
                                SetPlayButtonState(UpdateState.Play);
                            }
                            // Otherwise, if profile is NOT installable...
                            else if (IsProfileIndexValid() && profileList[profileIndex.Value].Installable == false)
                            {
                                // We delete the profile, because we can't install it and it therefor holds no value!
                                DeleteProfile(profileList[profileIndex.Value]);
                            }
                            // Otherwise, we still need to install.
                            else
                            {
                                SetPlayButtonState(UpdateState.Install);
                            }
                        }
                        else // We still need to select 1.1.
                        {
                            SetPlayButtonState(UpdateState.Select11);
                        }
                    }
                    else // We still need to download.
                    {
                        SetPlayButtonState(UpdateState.Download);
                    }
                }
                else
                {
                    playButton.Enabled = false;
                }
            }
        }

        /// <summary>
        /// Determines current conditions and enables or disables <see cref="apkButton"/> accordingly.
        /// </summary>
        private void UpdateApkState()
        {
            // Safety check
            if (apkButton != null)
            {
                // If profile supports Android...
                if (IsProfileIndexValid() && profileList[profileIndex.Value].SupportsAndroid && profileList[profileIndex.Value].Installable)
                {
                    // If we are NOT already creating an APK...
                    if (apkButtonState == ApkButtonState.Create)
                    {
                        // Switch status based on main button's state
                        switch (updateState)
                        {
                            case UpdateState.Download: apkButton.Enabled = false; apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip; break;
                            case UpdateState.Downloading: apkButton.Enabled = false; apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip; break;
                            case UpdateState.Select11: apkButton.Enabled = false; apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip; break;
                            case UpdateState.Install: apkButton.Enabled = true; apkButton.ToolTip = Language.Text.ApkButtonEnabledToolTip.Replace("$NAME", profileDropDown?.Items[profileDropDown.SelectedIndex]?.Text ?? ""); break;
                            case UpdateState.Installing: apkButton.Enabled = false; apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip; break;
                            case UpdateState.Play: apkButton.Enabled = true; apkButton.ToolTip = Language.Text.ApkButtonEnabledToolTip.Replace("$NAME", profileDropDown?.Items[profileDropDown.SelectedIndex]?.Text ?? ""); break;
                            case UpdateState.Playing: apkButton.Enabled = false; apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip; break;
                        }
                    }
                    else // Otherwise, disable.
                    {
                        apkButton.Enabled = false; 
                        apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip;
                    }
                }
                else // Otherwise, disable.
                {
                    apkButton.Enabled = false;
                    apkButton.ToolTip = Language.Text.ApkButtonDisabledToolTip;
                }
            }
        }

        /// <summary>
        /// Determines current conditions and enables or disables the <see cref="profileDropDown"/> and related controls accordingly.
        /// </summary>
        private void UpdateProfileState()
        {
            // Safety check
            if (profileDropDown != null)
            {
                switch (updateState)
                {
                    case UpdateState.Download: profileDropDown.Enabled = false; break;
                    case UpdateState.Downloading: profileDropDown.Enabled = false; break;
                    case UpdateState.Select11: profileDropDown.Enabled = true; break;
                    case UpdateState.Install: profileDropDown.Enabled = true; break;
                    case UpdateState.Installing: profileDropDown.Enabled = false; break;
                    case UpdateState.Play: profileDropDown.Enabled = true; break;
                    case UpdateState.Playing: profileDropDown.Enabled = false; break;
                }
                switch (apkButtonState)
                {
                    case ApkButtonState.Creating: profileDropDown.Enabled = false; break;
                }

                Color col = profileDropDown.Enabled ? colGreen : colInactive;

                if (Platform.IsWinForms)
                    profileDropDown.TextColor = col;
                profileAuthorLabel.TextColor = col;
                profileVersionLabel.TextColor = col;
                profileLabel.TextColor = col;
            }
        }

        /// <summary>
        /// Determines current conditions and enables or disables <see cref="profilePage"/> controls accordingly.
        /// </summary>
        private void UpdateProfileSettingsState()
        {
            // Safety check
            if (settingsProfileDropDown == null) return;
            if (settingsProfileDropDown.Items.Count > 0)
            {
                bool enabled = false;
                switch (updateState)
                {
                    case UpdateState.Download: enabled = false; break;
                    case UpdateState.Downloading: enabled = false; break;
                    case UpdateState.Select11: enabled = false; break;
                    case UpdateState.Install: enabled = true; break;
                    case UpdateState.Installing: enabled = false; break;
                    case UpdateState.Play: enabled = true; break;
                    case UpdateState.Playing: enabled = false; break;
                }
                if (apkButtonState == ApkButtonState.Creating) enabled = false;

                settingsProfileLabel.TextColor = colGreen;
                settingsProfileDropDown.Enabled = enabled;
                profileButton.Enabled = enabled;
                profileButton.ToolTip = Language.Text.OpenProfileFolderToolTip.Replace("$NAME", settingsProfileDropDown.Items[settingsProfileDropDown.SelectedIndex].Text);
                saveButton.Enabled = enabled;
                saveButton.ToolTip = Language.Text.OpenSaveFolderToolTip.Replace("$NAME", settingsProfileDropDown.Items[settingsProfileDropDown.SelectedIndex].Text);
                updateModButton.Enabled = enabled;
                updateModButton.ToolTip = Language.Text.UpdateModButtonToolTip.Replace("$NAME", settingsProfileDropDown.Items[settingsProfileDropDown.SelectedIndex].Text);
                deleteModButton.Enabled = enabled;
                deleteModButton.ToolTip = Language.Text.DeleteModButtonToolTip.Replace("$NAME", settingsProfileDropDown.Items[settingsProfileDropDown.SelectedIndex].Text);
                addModButton.Enabled = enabled;
                addModButton.ToolTip = Language.Text.AddNewModToolTip;

                Color col = enabled ? colGreen : colInactive;

                if (Platform.IsWinForms)
                    settingsProfileDropDown.TextColor = col;

                settingsProfileLabel.TextColor = col;

                if (enabled)
                {
                    settingsProfileDropDown.SelectedIndex = profileDropDown.SelectedIndex;
                    SettingsProfileDropDownSelectedIndexChanged(null, null);
                }
            }
        }

        /// <summary>
        /// This returns the text that <see cref="playButton"/> should have depending on the global updateState.
        /// </summary>
        /// <returns>The text as a <see cref="String"/>, or <see langword="null"/> if the current State is invalid.</returns>
        private string GetPlayButtonText()
        {
            switch (updateState)
            {
                case UpdateState.Download: return Language.Text.Download;
                case UpdateState.Downloading: return Language.Text.Abort;
                case UpdateState.Select11: return Language.Text.Select11;
                case UpdateState.Install: return Language.Text.Install;
                case UpdateState.Installing: return Language.Text.Installing;
                case UpdateState.Play: return Language.Text.Play;
                case UpdateState.Playing: return Language.Text.Playing;
            }
            return null;
        }

        /// <summary>
        /// This returns the text that the apkButton should have depending on the global updateState.
        /// </summary>
        /// <returns>The text as a <see cref="String"/>, or <see langword="null"/> if the current State is invalid.</returns>
        private string GetApkButtonText()
        {
            switch (apkButtonState)
            {
                case ApkButtonState.Create: return Language.Text.CreateAPK;
                case ApkButtonState.Creating: return Language.Text.CreatingAPK;
            }
            return null;
        }

        /// <summary>
        /// Sets the global <see cref="updateState"/> and then changes the state of <see cref="playButton"/> accordingly. 
        /// </summary>
        /// <param name="state">The state that should be set to.</param>
        private void SetPlayButtonState(UpdateState state)
        {
            updateState = state;
            string profileName = (profileDropDown != null && profileDropDown.Items.Count > 0) ? profileDropDown.Items[profileDropDown.SelectedIndex].Text : "";
            switch (updateState)
            {
                case UpdateState.Download: playButton.Enabled = true; playButton.ToolTip = Language.Text.PlayButtonDownloadToolTip; break;
                case UpdateState.Downloading: playButton.Enabled = true; playButton.ToolTip = ""; break; // ; playButton.ToolTip = Language.Text.PlayButtonDownladingToolTip; break;
                case UpdateState.Select11: playButton.Enabled = true; playButton.ToolTip = Language.Text.PlayButtonSelect11ToolTip; break;
                case UpdateState.Install: playButton.Enabled = true; playButton.ToolTip = Language.Text.PlayButtonInstallToolTip.Replace("$NAME", profileName) ; break;
                case UpdateState.Installing: playButton.Enabled = false; playButton.ToolTip = Language.Text.PlayButtonInstallingToolTip; break;
                case UpdateState.Play: playButton.Enabled = true; playButton.ToolTip = Language.Text.PlayButtonPlayToolTip.Replace("$NAME", profileName) ; break;
                case UpdateState.Playing: playButton.Enabled = false; playButton.ToolTip = Language.Text.PlayButtonPlayingToolTip; break;
            }
            playButton.Text = GetPlayButtonText();

            playButton.Invalidate();

            UpdateProfileSettingsState();
        }

        /// <summary>
        /// Sets the global <see cref="apkButtonState"/> and then changes the state of <see cref="apkButton"/> accordingly. 
        /// </summary>
        /// <param name="state">The state that should be set to.</param>
        private void SetApkButtonState(ApkButtonState state)
        {
            apkButtonState = state;
            switch (apkButtonState)
            {
                case ApkButtonState.Create: apkButton.Enabled = true; break;
                case ApkButtonState.Creating: apkButton.Enabled = false; break;
            }
            apkButton.Text = GetApkButtonText();
        }

        /// <summary>
        /// Checks if a Zip file is a valid AM2R_1.1 zip.
        /// </summary>
        /// <param name="zipPath">Full Path to the Zip file to check.</param>
        /// <returns><see cref="IsZipAM2R11ReturnCodes"/> detailing the result
        private IsZipAM2R11ReturnCodes CheckIfZipIsAM2R11(string zipPath)
        {
            const string d3dHash = "86e39e9161c3d930d93822f1563c280d";
            const string dataWinHash = "f2b84fe5ba64cb64e284be1066ca08ee";
            const string am2rHash = "15253f7a66d6ea3feef004ebbee9b438";

            string tmpPath = Path.GetTempPath() + Path.GetFileNameWithoutExtension(zipPath);

            // clean up in case folder exists already
            if (Directory.Exists(tmpPath))
                Directory.Delete(tmpPath, true);

            Directory.CreateDirectory(tmpPath);

            //open archive
            ZipArchive am2rZip = ZipFile.OpenRead(zipPath);

            // Check if exe exists anywhere
            ZipArchiveEntry am2rExe = am2rZip.Entries.FirstOrDefault(x => x.FullName.Contains("AM2R.exe"));
            if (am2rExe == null)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidAM2RExe;
            // Check if it's not in a subfolder. if it'd be in a subfolder, fullname would be "folder/AM2R.exe"
            if (am2rExe.FullName != "AM2R.exe")
                return IsZipAM2R11ReturnCodes.GameIsInASubfolder;
            // Check validity
            am2rExe.ExtractToFile(tmpPath + "/" + am2rExe.FullName);
            if (HelperMethods.CalculateMD5(tmpPath + "/" + am2rExe.FullName) != am2rHash)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidAM2RExe;

            // Check if data.win exists / is valid
            ZipArchiveEntry dataWin = am2rZip.Entries.FirstOrDefault(x => x.FullName == "data.win");
            if (dataWin == null)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidDataWin;
            dataWin.ExtractToFile(tmpPath + "/" + dataWin.FullName);
            if (HelperMethods.CalculateMD5(tmpPath + "/" + dataWin.FullName) != dataWinHash)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidDataWin;

            // Check if d3d.dll exists / is valid
            ZipArchiveEntry d3dx = am2rZip.Entries.FirstOrDefault(x => x.FullName == "D3DX9_43.dll");
            if (d3dx == null)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidD3DX9_43Dll;
            d3dx.ExtractToFile(tmpPath + "/" + d3dx.FullName);
            if (HelperMethods.CalculateMD5(tmpPath + "/" + d3dx.FullName) != d3dHash)
                return IsZipAM2R11ReturnCodes.MissingOrInvalidD3DX9_43Dll;

            // clean up
            Directory.Delete(tmpPath, true);

            // if we didn't exit before, everything is fine
            return IsZipAM2R11ReturnCodes.Successful;
        }
    }
}
