﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace XNAContentCompiler {

    public class ContentBuilder : IDisposable {
        // What importers or processors should we load?
        private const string XnaVersion = ", Version=4.0.0.0, PublicKeyToken=842cf8be1de50553";

        private static readonly string[] PipelineAssemblies = {
            "Microsoft.Xna.Framework.Content.Pipeline.FBXImporter" + XnaVersion,
            "Microsoft.Xna.Framework.Content.Pipeline.XImporter" + XnaVersion,
            "Microsoft.Xna.Framework.Content.Pipeline.TextureImporter" + XnaVersion,
            "Microsoft.Xna.Framework.Content.Pipeline.EffectImporter" + XnaVersion,
            "Microsoft.Xna.Framework.Content.Pipeline.AudioImporters" + XnaVersion,
            "Microsoft.Xna.Framework.Content.Pipeline.VideoImporters" + XnaVersion
        };

        private static int _directorySalt;
        private readonly ComboItemCollection _importers;

        private readonly List<ProjectItem> _projectItems = new List<ProjectItem>();
        private string _baseDirectory;
        private string _buildDirectory;
        private BuildParameters _buildParameters;
        private Project _buildProject;
        private ErrorLogger _errorLogger;


        private bool _isDisposed;
        private string _processDirectory;
        private ProjectRootElement _projectRootElement;

        /// <summary>
        ///     Gets the output directory, which will contain the generated .xnb files.
        /// </summary>
        public string OutputDirectory {
            get { return Path.Combine(_buildDirectory, "bin/Content"); }
        }


        /// <summary>
        ///     Creates a new content builder.
        /// </summary>
        public ContentBuilder() {
            CreateTempDirectory();
            CreateBuildProject();
            _importers = new ComboItemCollection {
                new ComboItem(".x", "XImporter", "ModelProcessor"), 
                new ComboItem(".fbx", "XmlImporter", "ModelProcessor"), 

                new ComboItem(".fx", "EffectImporter", "EffectProcessor"), 

                new ComboItem(".mp3", "Mp3Importer", "SongProcessor"), 
                new ComboItem(".wav", "WavImporter", "SoundEffectProcessor"),
                new ComboItem(".wma", "WmaImporter", "SongProcessor"), 

                new ComboItem(".bmp", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".jpg", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".png", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".tga", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".dds", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".dib", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".hdr", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".pfm", "TextureImporter", "TextureProcessor"), 
                new ComboItem(".ppm", "TextureImporter", "TextureProcessor"), 

                new ComboItem(".spritefont", "FontDescriptionImporter", "FontDescriptionProcessor")
            };

        }


        /// <summary>
        ///     Disposes the content builder when it is no longer required.
        /// </summary>
        public void Dispose() {
            Dispose(true);

            GC.SuppressFinalize(this);
        }


        /// <summary>
        ///     Implements the standard .NET IDisposable pattern.
        /// </summary>
        protected virtual void Dispose(bool disposing) {
            if (_isDisposed) {
                return;
            }

            _isDisposed = true;
            DeleteTempDirectory();
        }

        /// <summary>
        ///     Finalizes the content builder.
        /// </summary>
        ~ContentBuilder() {
            Dispose(false);
        }


        public void Add(ComboItem item) {
            var extension = Path.GetExtension(item.Value);
            if (extension == null) {
                return;
            }

            var importer = _importers.FindByName(extension.ToLower());
            Add(item.Value, Path.GetFileNameWithoutExtension(item.Name), importer.Value, importer.Other);
        }

        public void Add(string filename, string name) {
            Add(filename, name, null, null);
        }

        /// <summary>
        ///     Adds a new content file to the MSBuild project. The importer and
        ///     processor are optional: if you leave the importer null, it will
        ///     be autodetected based on the file extension, and if you leave the
        ///     processor null, data will be passed through without any processing.
        /// </summary>
        public void Add(string filename, string name, string importer, string processor) {
            if (filename == null) {
                throw new ArgumentNullException("filename");
            }

            var item = _buildProject.AddItem("Compile", filename)[0];

            item.SetMetadataValue("Link", Path.GetFileName(filename));
            item.SetMetadataValue("Name", name);

            if (!string.IsNullOrEmpty(importer)) {
                item.SetMetadataValue("Importer", importer);
            }

            if (!string.IsNullOrEmpty(processor)) {
                item.SetMetadataValue("Processor", processor);
            }

            _projectItems.Add(item);
        }


        /// <summary>
        ///     Removes all content files from the MSBuild project.
        /// </summary>
        public void Clear() {
            _buildProject.RemoveItems(_projectItems);

            _projectItems.Clear();
        }


        /// <summary>
        ///     Builds all the content files which have been added to the project,
        ///     dynamically creating .xnb files in the OutputDirectory.
        ///     Returns an error message if the build fails.
        /// </summary>
        public string Build() {
            // Clear any previous errors.
            _errorLogger.Errors.Clear();

            // Create and submit a new asynchronous build request.
            BuildManager.DefaultBuildManager.BeginBuild(_buildParameters);

            var request = new BuildRequestData(_buildProject.CreateProjectInstance(), new string[0]);
            var submission = BuildManager.DefaultBuildManager.PendBuildRequest(request);

            submission.ExecuteAsync(null, null);

            // Wait for the build to finish.
            submission.WaitHandle.WaitOne();

            BuildManager.DefaultBuildManager.EndBuild();

            // If the build failed, return an error string.
            if (submission.BuildResult.OverallResult == BuildResultCode.Failure) {
                return string.Join("\n", _errorLogger.Errors.ToArray());
            }

            return null;
        }

        /// <summary>
        ///     Creates a temporary MSBuild content project in memory.
        /// </summary>
        private void CreateBuildProject() {
            string projectPath = Path.Combine(_buildDirectory, "content.contentproj");
            string outputPath = Path.Combine(_buildDirectory, "bin");

            // Create the build project.
            _projectRootElement = ProjectRootElement.Create(projectPath);

            // Include the standard targets file that defines how to build XNA Framework content.
            _projectRootElement.AddImport("$(MSBuildExtensionsPath)\\Microsoft\\XNA Game Studio\\" +
                                         "v4.0\\Microsoft.Xna.GameStudio.ContentPipeline.targets");

            _buildProject = new Project(_projectRootElement);

            _buildProject.SetProperty("XnaPlatform", "Windows");
            _buildProject.SetProperty("XnaProfile", "Reach");
            _buildProject.SetProperty("XnaFrameworkVersion", "v4.0");
            _buildProject.SetProperty("Configuration", "Release");
            _buildProject.SetProperty("OutputPath", outputPath);

            // Register any custom importers or processors.
            foreach (var pipelineAssembly in PipelineAssemblies) {
                _buildProject.AddItem("Reference", pipelineAssembly);
            }

            // Hook up our custom error logger.
            _errorLogger = new ErrorLogger();

            _buildParameters = new BuildParameters(ProjectCollection.GlobalProjectCollection) {
                Loggers = new ILogger[] {_errorLogger}
            };
        }


        /// <summary>
        ///     Creates a temporary directory in which to build content.
        /// </summary>
        private void CreateTempDirectory() {
            // Start with a standard base name:
            //
            //  %temp%\WinFormsContentLoading.ContentBuilder

            _baseDirectory = Path.Combine(Path.GetTempPath(), GetType().FullName);

            // Include our process ID, in case there is more than
            // one copy of the program running at the same time:
            //
            //  %temp%\WinFormsContentLoading.ContentBuilder\<ProcessId>

            var processId = Process.GetCurrentProcess().Id;
            _processDirectory = Path.Combine(_baseDirectory, processId.ToString(CultureInfo.InvariantCulture));

            // Include a salt value, in case the program
            // creates more than one ContentBuilder instance:
            //
            //  %temp%\WinFormsContentLoading.ContentBuilder\<ProcessId>\<Salt>
            _directorySalt++;
            _buildDirectory = Path.Combine(_processDirectory, _directorySalt.ToString(CultureInfo.InvariantCulture));

            // Create our temporary directory.
            Directory.CreateDirectory(_buildDirectory);

            PurgeStaleTempDirectories();
        }


        /// <summary>
        ///     Deletes our temporary directory when we are finished with it.
        /// </summary>
        private void DeleteTempDirectory() {
            Directory.Delete(_buildDirectory, true);

            // If there are no other instances of ContentBuilder still using their
            // own temp directories, we can delete the process directory as well.
            if (Directory.GetDirectories(_processDirectory).Length != 0) {
                return;
            }

            Directory.Delete(_processDirectory);

            // If there are no other copies of the program still using their
            // own temp directories, we can delete the base directory as well.
            if (Directory.GetDirectories(_baseDirectory).Length == 0) {
                Directory.Delete(_baseDirectory);
            }
        }


        /// <summary>
        ///     Ideally, we want to delete our temp directory when we are finished using
        ///     it. The DeleteTempDirectory method (called by whichever happens first out
        ///     of Dispose or our finalizer) does exactly that. Trouble is, sometimes
        ///     these cleanup methods may never execute. For instance if the program
        ///     crashes, or is halted using the debugger, we never get a chance to do
        ///     our deleting. The next time we start up, this method checks for any temp
        ///     directories that were left over by previous runs which failed to shut
        ///     down cleanly. This makes sure these orphaned directories will not just
        ///     be left lying around forever.
        /// </summary>
        private void PurgeStaleTempDirectories() {
            // Check all subdirectories of our base location.
            foreach (var directory in Directory.GetDirectories(_baseDirectory)) {
                // The subdirectory name is the ID of the process which created it.
                int processId;

                if (int.TryParse(Path.GetFileName(directory), out processId)) {
                    try {
                        // Is the creator process still running?
                        Process.GetProcessById(processId);
                    } catch (ArgumentException) {
                        // If the process is gone, we can delete its temp directory.
                        Directory.Delete(directory, true);
                    }
                }
            }
        }

    }

}