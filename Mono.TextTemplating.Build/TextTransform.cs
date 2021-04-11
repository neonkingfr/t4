// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using MessagePack;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.VisualStudio.TextTemplating;

namespace Mono.TextTemplating.Build
{
	// parameter values:
	// VS TextTemplatingFileGenerator tool: all MSBuild variables from the project, but not T4ParameterValue items
	// VS MSBuild targets: T4ParameterValue items, but not arbitrary MSBuild properties (ironically)
	// maybe we should add some common ones by default?
	public class TextTransform : Task
	{
		public string DefaultNamespace { get; set; }
		public ITaskItem [] PreprocessTemplates { get; set; }
		public ITaskItem [] TransformTemplates { get; set; }
		public ITaskItem [] IncludePaths { get; set; }
		public ITaskItem [] DirectiveProcessors { get; set; }
		public ITaskItem [] AssemblyReferences { get; set; }
		public ITaskItem [] ReferencePaths { get; set; }

		public ITaskItem [] ParameterValues { get; set; }

		public bool PreprocessOnly { get; set; }
		public bool UseLegacyPreprocessingMode { get; set; }

		[Required]
		public string IntermediateDirectory { get; set; }

		[Output]
		public ITaskItem [] RequiredAssemblies { get; set; }

		[Output]
		public ITaskItem [] TransformTemplateOutput { get; set; }

		[Output]
		public ITaskItem [] PreprocessedTemplateOutput { get; set; }

		public override bool Execute ()
		{
			bool success = true;

			Directory.CreateDirectory (IntermediateDirectory);

			string buildStateFilename = Path.Combine (IntermediateDirectory, "t4-build-state.msgpack");

			var msgPackOptions = MessagePackSerializerOptions.Standard
				.WithAllowAssemblyVersionMismatch (false)
				.WithCompression (MessagePackCompression.Lz4BlockArray)
				.WithSecurity (MessagePackSecurity.TrustedData);

			var previousBuildState = LoadBuildState (buildStateFilename, msgPackOptions);

			var buildState = new TemplateBuildState {
				IntermediateDirectory = IntermediateDirectory,
				DefaultNamespace = DefaultNamespace
			};

			success &= AddParameters (buildState);
			success &= AddDirectiveProcessors (buildState);

			if (!success) {
				return false;
			}

			if (IncludePaths != null) {
				buildState.IncludePaths = new List<string> (IncludePaths.Select (i => i.ItemSpec));
			}

			if (ReferencePaths != null) {
				buildState.ReferencePaths = new List<string> (ReferencePaths.Select (i => i.ItemSpec));
			}

			if (AssemblyReferences != null) {
				buildState.AssemblyReferences = new List<string> (AssemblyReferences.Select (i => i.ItemSpec));
			}

			if (PreprocessTemplates != null) {
				buildState.PreprocessTemplates = new List<TemplateBuildState.PreprocessedTemplate> ();
				foreach (var ppt in PreprocessTemplates) {
					string inputFile = ppt.ItemSpec;
					string outputFile;
					if (UseLegacyPreprocessingMode) {
						outputFile = Path.ChangeExtension (inputFile, ".cs");
					} else {
						//FIXME: this could cause collisions. generate a path based on relative path and link metadata
						outputFile = Path.Combine (IntermediateDirectory, Path.ChangeExtension (inputFile, ".cs"));
					}
					buildState.PreprocessTemplates.Add (new TemplateBuildState.PreprocessedTemplate {
						InputFile = inputFile,
						OutputFile = outputFile
					});
				}
			}

			if (TransformTemplates != null) {
				buildState.TransformTemplates = new List<TemplateBuildState.TransformTemplate> ();
				foreach (var tt in TransformTemplates) {
					string inputFile = tt.ItemSpec;
					string outputFile = Path.ChangeExtension (inputFile, ".txt");
					buildState.TransformTemplates.Add (new TemplateBuildState.TransformTemplate {
						InputFile = inputFile,
						OutputFile = outputFile
					});
				}
			}

			var processor = new TextTransformProcessor (Log);
			processor.Process (previousBuildState, buildState, PreprocessOnly);

			if (buildState.TransformTemplates != null) {
				TransformTemplateOutput = new ITaskItem[buildState.TransformTemplates.Count];
				for (int i = 0; i < buildState.TransformTemplates.Count; i++) {
					TransformTemplateOutput[i] = new TaskItem (buildState.TransformTemplates[i].OutputFile);
				}
			}

			if (buildState.PreprocessTemplates != null) {
				PreprocessedTemplateOutput = new ITaskItem[buildState.PreprocessTemplates.Count];
				for (int i = 0; i < buildState.PreprocessTemplates.Count; i++) {
					PreprocessedTemplateOutput[i] = new TaskItem (buildState.PreprocessTemplates[i].OutputFile);
				}
			}

			//TODO
			//IntermediateDirectory
			//RequiredAssemblies
			//GeneratedTemplates
			//PreprocessedTemplates
			//settings.Debug
			//settings.Log
			//metadata to override output name, class name and namespace

			SaveBuildState (buildState, buildStateFilename, msgPackOptions);

			//var stateJson = MessagePackSerializer.ConvertToJson (File.ReadAllBytes (buildStateFilename), msgPackOptions);

			return success;
		}

		bool AddParameters (TemplateBuildState buildState)
		{
			bool success = true;

			if (ParameterValues == null) {
				return true;
			}

			buildState.Parameters = new List<TemplateBuildState.Parameter> ();

			foreach (var par in ParameterValues) {
				string paramName = par.ItemSpec;

				string paramVal = par.GetMetadata ("Value");
				string processorName, directiveName;

				if (!string.IsNullOrEmpty (paramVal)) {
					processorName = par.GetMetadata ("Processor");
					directiveName = par.GetMetadata ("Directive");
				}
				else if (!TemplateGenerator.TryParseParameter (paramName, out processorName, out directiveName, out paramName, out paramVal)) {
					Log.LogError ("Parameter does not have Value metadata or encoded value: {0}", par);
					success = false;
					continue;
				}

				buildState.Parameters.Add (new TemplateBuildState.Parameter {
					Processor = processorName,
					Directive = directiveName,
					Name = paramName,
					Value = paramVal
				});
			}

			return success;
		}

		bool AddDirectiveProcessors (TemplateBuildState buildState)
		{
			if (DirectiveProcessors == null) {
				return true;
			}

			buildState.DirectiveProcessors = new List<TemplateBuildState.DirectiveProcessor> ();

			bool hasErrors = false;

			foreach (var dirItem in DirectiveProcessors) {

				var name = dirItem.ItemSpec;
				var className = dirItem.GetMetadata ("Class");

				if (className != null) {
					var assembly = dirItem.GetMetadata ("Assembly") ?? dirItem.GetMetadata ("Codebase");
					if (string.IsNullOrEmpty (assembly)) {
						Log.LogError ("Directive '{0}' is missing 'Assembly' metadata", name);
						hasErrors = true;
					}

					buildState.DirectiveProcessors.Add (new TemplateBuildState.DirectiveProcessor {
						Name = name,
						Class = className,
						Assembly = assembly
					});
					continue;
				}

				var split = name.Split ('!');
				if (split.Length != 3) {
					Log.LogError ("Directive must have 3 values: {0}", name);
					hasErrors = true;
					continue;
				}

				for (int i = 0; i < 3; i++) {
					string s = split[i];
					if (string.IsNullOrEmpty (s)) {
						string kind = i == 0 ? "name" : (i == 1 ? "class" : "assembly");
						Log.LogError ("Directive has missing {0} value: {1}", kind, name);
						hasErrors = true;
						continue;
					}
				}

				buildState.DirectiveProcessors.Add (new TemplateBuildState.DirectiveProcessor {
					Name = split[0],
					Class = split[1],
					Assembly = split[2]
				});
			}

			return !hasErrors;
		}

		TemplateBuildState LoadBuildState (string filePath, MessagePackSerializerOptions options)
		{
			if (!File.Exists(filePath)) {
				return null;
			}

			try {
				using var stream = File.OpenRead (filePath);

				var state =  MessagePackSerializer.Deserialize<TemplateBuildState> (stream, options);

				if (state.FormatVersion != TemplateBuildState.CURRENT_FORMAT_VERSION) {
					Log.LogMessage (MessageImportance.Low, "T4 build state format has changed. All T4 files will be reprocessed.");
				}

				return state;
			}
			catch (MessagePackSerializationException) {
				Log.LogMessage (MessageImportance.Low, "T4 build state could not be deserialized. The format may have changed. All T4 files will be reprocessed.");
			}
			catch (Exception ex) {
				//FIXME: better handling here
				Log.LogWarning ("Failed to load T4 build state. All T4 files will be reprocessed.");
				Log.LogMessage (MessageImportance.Low, ex.ToString());
			}

			return null;
		}

		void SaveBuildState (TemplateBuildState buildState, string filePath, MessagePackSerializerOptions options)
		{
			try {
				using var stream = File.Create (filePath);
				MessagePackSerializer.Serialize (stream, buildState, options);
			}
			catch (Exception ex) {
				//FIXME: better handling here
				Log.LogWarning ("Failed to save T4 build state. The next build will not be incremental.");
				Log.LogMessage (MessageImportance.Low, ex.ToString ());
				try {
					File.Delete (filePath);
				}
				catch {
				}
			}
		}
	}
}