#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

/// <summary>
/// Request accepted by the headless Embed for Edit API.
/// </summary>
[Serializable]
public sealed class ActionFitPackageEmbedRequest
{
    public string PackageId;
    public bool DryRun;
    public bool Resolve = true;
    public bool AllowExistingLocalFolder = true;
}

/// <summary>
/// Structured result returned by validation and Embed for Edit operations.
/// </summary>
[Serializable]
public sealed class ActionFitPackageEmbedResult
{
    public bool Success;
    public bool DryRun;
    public bool Changed;
    public bool RolledBack;
    public bool RecoveryRequired;
    public string Code;
    public string Message;
    public string PackageId;
    public string SourceVersion;
    public string SourcePath;
    public string EmbeddedPath;
    public string ManifestPath;
    public string PreviousDependency;
    public string NewDependency;
    public string JournalPath;
    public string[] ChangedFiles = Array.Empty<string>();
    public string[] Warnings = Array.Empty<string>();
}

/// <summary>
/// Downloaded package that can be passed to the Embed for Edit API.
/// </summary>
[Serializable]
public sealed class ActionFitPackageEmbedCandidate
{
    public string PackageId;
    public string Version;
    public string SourcePath;
    public bool CanEmbed;
    public string Reason;
}

/// <summary>
/// Public, dialog-free API shared by the Package Manager UI, AI tools, and batch commands.
/// </summary>
public static class ActionFitPackageEmbedApi
{
    /// <summary>
    /// Lists installed ActionFit packages and whether each package can be embedded.
    /// </summary>
    public static ActionFitPackageEmbedCandidate[] GetCandidates()
    {
        try
        {
            return UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package != null && package.name.StartsWith("com.actionfit.", StringComparison.Ordinal))
                .Select(package =>
                {
                    bool downloaded = IsDownloadedSource(package.name, package.resolvedPath);
                    return new ActionFitPackageEmbedCandidate
                    {
                        PackageId = package.name,
                        Version = package.version,
                        SourcePath = ActionFitPackagePaths.ToProjectRelativePath(package.resolvedPath),
                        CanEmbed = downloaded,
                        Reason = downloaded ? "Downloaded package source is available." : "Package is already local or its resolved source is unavailable.",
                    };
                })
                .OrderBy(candidate => candidate.PackageId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Failed to list embed candidates: {ex.Message}");
            return Array.Empty<ActionFitPackageEmbedCandidate>();
        }
    }

    /// <summary>
    /// Validates an Embed for Edit request without changing project files.
    /// </summary>
    public static ActionFitPackageEmbedResult Validate(ActionFitPackageEmbedRequest request)
    {
        return TryBuildContext(request, out EmbedContext context, out ActionFitPackageEmbedResult failure)
            ? BuildValidationResult(request, context)
            : failure;
    }

    /// <summary>
    /// Embeds a downloaded package using a recoverable filesystem and manifest transaction.
    /// </summary>
    public static ActionFitPackageEmbedResult EmbedForEdit(ActionFitPackageEmbedRequest request)
    {
        return EmbedForEdit(request, null);
    }

    /// <summary>
    /// Executes an Embed for Edit request encoded with Unity JsonUtility and returns JSON.
    /// </summary>
    public static string ExecuteJson(string requestJson)
    {
        try
        {
            var request = JsonUtility.FromJson<ActionFitPackageEmbedRequest>(requestJson ?? "");
            return JsonUtility.ToJson(EmbedForEdit(request), true);
        }
        catch (Exception ex)
        {
            return JsonUtility.ToJson(Failure(null, "INVALID_REQUEST_JSON", ex.Message), true);
        }
    }

    /// <summary>
    /// Recovers any interrupted package transactions and returns structured results.
    /// </summary>
    public static ActionFitPackageRecoveryResult[] RecoverPendingTransactions()
    {
        return ActionFitPackageTransaction.RecoverPendingTransactions();
    }

    internal static ActionFitPackageEmbedResult EmbedForEdit(
        ActionFitPackageEmbedRequest request,
        Action<string> prepareDestination)
    {
        if (!TryBuildContext(request, out EmbedContext context, out ActionFitPackageEmbedResult failure))
            return failure;

        if (context.AlreadyEmbedded)
        {
            return new ActionFitPackageEmbedResult
            {
                Success = true,
                DryRun = request.DryRun,
                Code = "ALREADY_EMBEDDED",
                Message = $"{request.PackageId} already uses a valid local file dependency.",
                PackageId = request.PackageId,
                SourceVersion = context.SourceVersion,
                SourcePath = ActionFitPackagePaths.ToProjectRelativePath(context.SourcePath),
                EmbeddedPath = ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath),
                ManifestPath = ActionFitPackagePaths.ToProjectRelativePath(ActionFitPackagePaths.ManifestPath),
                PreviousDependency = context.PreviousDependency,
                NewDependency = context.NewDependency,
            };
        }

        if (request.DryRun)
        {
            ActionFitPackageEmbedResult dryRun = BuildValidationResult(request, context);
            dryRun.Code = "DRY_RUN";
            dryRun.DryRun = true;
            dryRun.Message = $"Embed for Edit validation passed for {request.PackageId}; no files were changed.";
            return dryRun;
        }

        var transaction = ActionFitPackageTransaction.Execute(new ActionFitPackageTransaction.Request
        {
            Operation = "Embed for Edit",
            PackageId = request.PackageId,
            SourcePath = context.SourcePath,
            DestinationPath = context.DestinationPath,
            OriginalManifest = context.OriginalManifest,
            UpdatedManifest = context.UpdatedManifest,
            AffectedPackageIds = new[] { request.PackageId },
            UseExistingDestination = context.UseExistingDestination,
            PrepareDestination = prepareDestination,
        });

        if (!transaction.Success)
        {
            ActionFitPackageEmbedResult result = Failure(request.PackageId, transaction.Code, transaction.Message);
            result.RolledBack = transaction.RolledBack;
            result.RecoveryRequired = transaction.RecoveryRequired;
            result.JournalPath = ActionFitPackagePaths.ToProjectRelativePath(transaction.JournalPath);
            result.SourceVersion = context.SourceVersion;
            result.SourcePath = ActionFitPackagePaths.ToProjectRelativePath(context.SourcePath);
            result.EmbeddedPath = ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath);
            result.PreviousDependency = context.PreviousDependency;
            result.NewDependency = context.NewDependency;
            return result;
        }

        var warnings = new List<string>();
        try
        {
            ActionFitPackageAiGuideRouter.EnsureProjectRouter();
        }
        catch (Exception ex)
        {
            warnings.Add($"AI guide router refresh failed: {ex.Message}");
        }

        try
        {
            ActionFitPackageBaseline.Save(request.PackageId, context.DestinationPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"Embedded baseline save failed: {ex.Message}");
        }

        if (request.Resolve)
            Client.Resolve();
        AssetDatabase.Refresh();

        Debug.Log(
            $"[ActionFitPackageManager] Embedded package for edit: {request.PackageId}\n" +
            $"Source: {ActionFitPackagePaths.ToProjectRelativePath(context.SourcePath)}\n" +
            $"Destination: {ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath)}");

        return new ActionFitPackageEmbedResult
        {
            Success = true,
            Changed = true,
            Code = "EMBEDDED",
            Message = $"{request.PackageId} was embedded for edit.",
            PackageId = request.PackageId,
            SourceVersion = context.SourceVersion,
            SourcePath = ActionFitPackagePaths.ToProjectRelativePath(context.SourcePath),
            EmbeddedPath = ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath),
            ManifestPath = ActionFitPackagePaths.ToProjectRelativePath(ActionFitPackagePaths.ManifestPath),
            PreviousDependency = context.PreviousDependency,
            NewDependency = context.NewDependency,
            JournalPath = ActionFitPackagePaths.ToProjectRelativePath(transaction.JournalPath),
            ChangedFiles = new[]
            {
                ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath),
                ActionFitPackagePaths.ToProjectRelativePath(ActionFitPackagePaths.ManifestPath),
            },
            Warnings = warnings.ToArray(),
        };
    }

    private static bool TryBuildContext(
        ActionFitPackageEmbedRequest request,
        out EmbedContext context,
        out ActionFitPackageEmbedResult failure)
    {
        context = null;
        failure = null;
        try
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ActionFitPackagePaths.ValidatePackageId(request.PackageId);
            if (!request.PackageId.StartsWith("com.actionfit.", StringComparison.Ordinal))
                throw new InvalidOperationException("Embed for Edit API is limited to com.actionfit.* packages.");
            if (!File.Exists(ActionFitPackagePaths.ManifestPath))
                throw new FileNotFoundException("Packages/manifest.json was not found.", ActionFitPackagePaths.ManifestPath);

            string manifest = File.ReadAllText(ActionFitPackagePaths.ManifestPath);
            ActionFitPackageManifestUtility.Validate(manifest);
            string previousDependency = ActionFitPackageManifestUtility.GetDependency(manifest, request.PackageId);
            if (string.IsNullOrWhiteSpace(previousDependency))
                throw new InvalidOperationException($"{request.PackageId} is not a direct dependency in Packages/manifest.json.");

            string destinationPath = ActionFitPackagePaths.PackagePath(request.PackageId);
            string localDependency = ActionFitPackageManifestUtility.LocalDependency(request.PackageId);
            bool destinationExists = ActionFitPackageFileUtility.PhysicalDirectoryExists(destinationPath);
            string destinationError = "";
            bool destinationValid = destinationExists && ActionFitPackageFileUtility.IsValidLocalPackageFolder(
                request.PackageId,
                destinationPath,
                out destinationError);
            bool manifestIsLocal = previousDependency.Trim().StartsWith("file:", StringComparison.OrdinalIgnoreCase);

            if (manifestIsLocal && !destinationValid)
                throw new InvalidOperationException($"Manifest already points to a local package, but the folder is invalid: {destinationError}");

            if (manifestIsLocal)
            {
                ActionFitPackageManifest localManifest = ActionFitPackageManifest.Read(Path.Combine(destinationPath, "package.json"));
                context = new EmbedContext
                {
                    AlreadyEmbedded = true,
                    UseExistingDestination = true,
                    DestinationPath = destinationPath,
                    SourcePath = destinationPath,
                    SourceVersion = localManifest.Version,
                    PreviousDependency = previousDependency,
                    NewDependency = localDependency,
                    OriginalManifest = manifest,
                    UpdatedManifest = manifest,
                };
                return true;
            }

            if (destinationExists && !destinationValid)
                throw new InvalidOperationException(destinationError);
            if (destinationValid && !request.AllowExistingLocalFolder)
                throw new InvalidOperationException($"A valid local package folder already exists: {ActionFitPackagePaths.ToProjectRelativePath(destinationPath)}");

            string sourcePath;
            string sourceVersion;
            if (destinationValid)
            {
                sourcePath = destinationPath;
                sourceVersion = ActionFitPackageManifest.Read(Path.Combine(destinationPath, "package.json")).Version;
            }
            else
            {
                if (!TryFindDownloadedSource(request.PackageId, out sourcePath, out string sourceError))
                    throw new InvalidOperationException(sourceError);
                sourceVersion = ActionFitPackageManifest.Read(Path.Combine(sourcePath, "package.json")).Version;
            }

            context = new EmbedContext
            {
                UseExistingDestination = destinationValid,
                DestinationPath = destinationPath,
                SourcePath = sourcePath,
                SourceVersion = sourceVersion,
                PreviousDependency = previousDependency,
                NewDependency = localDependency,
                OriginalManifest = manifest,
                UpdatedManifest = ActionFitPackageManifestUtility.SetDependency(manifest, request.PackageId, localDependency),
            };
            return true;
        }
        catch (Exception ex)
        {
            failure = Failure(request?.PackageId, "VALIDATION_FAILED", ex.Message);
            return false;
        }
    }

    private static bool TryFindDownloadedSource(string packageId, out string sourcePath, out string error)
    {
        sourcePath = "";
        error = "";
        try
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo
                .GetAllRegisteredPackages()
                .FirstOrDefault(info => string.Equals(info.name, packageId, StringComparison.Ordinal));
            if (package != null && IsDownloadedSource(packageId, package.resolvedPath))
            {
                sourcePath = Path.GetFullPath(package.resolvedPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ActionFitPackageManager] Registered package lookup failed for {packageId}: {ex.Message}");
        }

        string cacheRoot = Path.Combine(ActionFitPackagePaths.ProjectRoot, "Library", "PackageCache");
        if (Directory.Exists(cacheRoot))
        {
            foreach (string directory in Directory.GetDirectories(cacheRoot, packageId + "@*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                if (!IsDownloadedSource(packageId, directory)) continue;
                sourcePath = Path.GetFullPath(directory);
                return true;
            }
        }

        error = $"Downloaded package source was not found for {packageId}. Run Unity Package Manager resolve and try again.";
        return false;
    }

    private static bool IsDownloadedSource(string packageId, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ActionFitPackageFileUtility.PhysicalDirectoryExists(path)) return false;
        string relativePath = ActionFitPackagePaths.ToProjectRelativePath(path).Replace("\\", "/");
        if (relativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return false;
        return ActionFitPackageFileUtility.IsValidLocalPackageFolder(packageId, path, out _);
    }

    private static ActionFitPackageEmbedResult BuildValidationResult(ActionFitPackageEmbedRequest request, EmbedContext context)
    {
        return new ActionFitPackageEmbedResult
        {
            Success = true,
            DryRun = request.DryRun,
            Code = context.AlreadyEmbedded ? "ALREADY_EMBEDDED" : "READY",
            Message = context.AlreadyEmbedded
                ? $"{request.PackageId} already uses a valid local package folder."
                : $"{request.PackageId} is ready to embed for edit.",
            PackageId = request.PackageId,
            SourceVersion = context.SourceVersion,
            SourcePath = ActionFitPackagePaths.ToProjectRelativePath(context.SourcePath),
            EmbeddedPath = ActionFitPackagePaths.ToProjectRelativePath(context.DestinationPath),
            ManifestPath = ActionFitPackagePaths.ToProjectRelativePath(ActionFitPackagePaths.ManifestPath),
            PreviousDependency = context.PreviousDependency,
            NewDependency = context.NewDependency,
        };
    }

    private static ActionFitPackageEmbedResult Failure(string packageId, string code, string message)
    {
        return new ActionFitPackageEmbedResult
        {
            Success = false,
            Code = code,
            Message = message,
            PackageId = packageId ?? "",
            ManifestPath = ActionFitPackagePaths.ToProjectRelativePath(ActionFitPackagePaths.ManifestPath),
        };
    }

    private sealed class EmbedContext
    {
        public bool AlreadyEmbedded;
        public bool UseExistingDestination;
        public string SourcePath;
        public string SourceVersion;
        public string DestinationPath;
        public string OriginalManifest;
        public string UpdatedManifest;
        public string PreviousDependency;
        public string NewDependency;
    }
}

/// <summary>
/// Batchmode entry point for AI and CI callers that use request and result JSON files.
/// </summary>
public static class ActionFitPackageEmbedCli
{
    /// <summary>
    /// Runs with -actionFitEmbedRequest &lt;path&gt; and -actionFitEmbedResult &lt;path&gt;.
    /// </summary>
    public static void Run()
    {
        string requestPath = GetArgument("-actionFitEmbedRequest");
        string resultPath = GetArgument("-actionFitEmbedResult");
        ActionFitPackageEmbedResult result;

        try
        {
            if (string.IsNullOrWhiteSpace(requestPath))
                throw new InvalidOperationException("-actionFitEmbedRequest is required.");
            if (string.IsNullOrWhiteSpace(resultPath))
                throw new InvalidOperationException("-actionFitEmbedResult is required.");

            var request = JsonUtility.FromJson<ActionFitPackageEmbedRequest>(File.ReadAllText(Path.GetFullPath(requestPath)));
            result = ActionFitPackageEmbedApi.EmbedForEdit(request);
        }
        catch (Exception ex)
        {
            result = new ActionFitPackageEmbedResult
            {
                Success = false,
                Code = "CLI_FAILED",
                Message = ex.Message,
            };
        }

        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            string fullResultPath = Path.GetFullPath(resultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath));
            File.WriteAllText(fullResultPath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
        }

        if (result.Success) Debug.Log($"[ActionFitPackageManager] Embed CLI succeeded: {result.Message}");
        else Debug.LogError($"[ActionFitPackageManager] Embed CLI failed: {result.Code} - {result.Message}");
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return "";
    }
}
#endif
