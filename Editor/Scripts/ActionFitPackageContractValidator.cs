#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class ActionFitPackageContractDiagnostic
{
    public string Code;
    public string Severity;
    public string Path;
    public int Line;
    public string Message;
    public string SuggestedFix;

    internal string ToSummary()
        => $"{Code}: {Message} ({Path}:{Line})";
}

internal sealed class ActionFitPackageContractValidationResult
{
    public bool Success;
    public int ExitCode;
    public string Code;
    public string Message;
    public string RawJson;
    public ActionFitPackageContractDiagnostic[] Diagnostics = Array.Empty<ActionFitPackageContractDiagnostic>();

    internal static ActionFitPackageContractValidationResult Passed()
    {
        return new ActionFitPackageContractValidationResult
        {
            Success = true,
            ExitCode = 0,
            Code = "PACKAGE_CONTRACT_VALID",
            Message = "Package contract validation passed.",
        };
    }

    internal static ActionFitPackageContractValidationResult Failed(
        string code,
        string message,
        int exitCode = 1,
        params ActionFitPackageContractDiagnostic[] diagnostics)
    {
        return new ActionFitPackageContractValidationResult
        {
            Success = false,
            ExitCode = exitCode,
            Code = code,
            Message = message,
            Diagnostics = diagnostics ?? Array.Empty<ActionFitPackageContractDiagnostic>(),
        };
    }
}

/// <summary>
/// Runs the package-owned Python contract validator without Unity, catalog, credential, or network dependencies.
/// </summary>
internal static class ActionFitPackageContractValidator
{
    private const string PackageId = "com.actionfit.custompackagemanager";
    private const int ProcessTimeoutMilliseconds = 60000;
    internal static Func<string, ActionFitPackageContractValidationResult> ValidatePackageOverride;

    [Serializable]
    private sealed class CliDiagnosticDto
    {
        public string code;
        public string severity;
        public string path;
        public int line;
        public string message;
        public string suggestedFix;
    }

    [Serializable]
    private sealed class CliResultDto
    {
        public bool success;
        public int exitCode;
        public CliDiagnosticDto[] diagnostics;
    }

    private sealed class InterpreterCandidate
    {
        public string FileName;
        public string PrefixArguments;
    }

    /// <summary>
    /// Validates one embedded ActionFit package with the exact package-owned CLI contract.
    /// </summary>
    internal static ActionFitPackageContractValidationResult ValidatePackage(string packageId)
    {
        if (ValidatePackageOverride != null)
            return ValidatePackageOverride(packageId);

        try
        {
            ActionFitPackagePaths.ValidatePackageId(packageId);
            if (!packageId.StartsWith("com.actionfit.", StringComparison.Ordinal))
            {
                return InfrastructureFailure($"Contract validation is limited to com.actionfit.* packages: {packageId}");
            }

            string packageRoot = ResolvePackageRoot();
            string scriptPath = Path.Combine(packageRoot, "Tools~", "package_contract_validator.py");
            if (!File.Exists(scriptPath))
                return InfrastructureFailure($"Package contract validator was not found: {scriptPath}");
            string repositoryRoot = ResolveRepositoryRoot(packageRoot);

            var launchErrors = new List<string>();
            foreach (InterpreterCandidate candidate in GetInterpreterCandidates())
            {
                if (TryRun(
                        candidate,
                        scriptPath,
                        packageId,
                        repositoryRoot,
                        out ActionFitPackageContractValidationResult result,
                        out string error))
                    return result;
                launchErrors.Add(error);
            }

            return InfrastructureFailure(
                "Python 3.9+ could not run the package contract validator. " + string.Join(" | ", launchErrors));
        }
        catch (Exception ex)
        {
            return InfrastructureFailure($"Package contract validation failed to start: {ex.Message}");
        }
    }

    internal static string[] DiagnosticSummaries(ActionFitPackageContractValidationResult result)
        => result?.Diagnostics?.Select(item => item.ToSummary()).ToArray() ?? Array.Empty<string>();

    private static bool TryRun(
        InterpreterCandidate candidate,
        string scriptPath,
        string packageId,
        string repositoryRoot,
        out ActionFitPackageContractValidationResult result,
        out string error)
    {
        result = null;
        error = "";
        string arguments = string.Join(" ", new[]
        {
            candidate.PrefixArguments,
            Quote(scriptPath),
            "--package",
            Quote(packageId),
            "--repo-root",
            Quote(repositoryRoot),
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        try
        {
            var startInfo = new ProcessStartInfo(candidate.FileName, arguments)
            {
                WorkingDirectory = ActionFitPackagePaths.ProjectRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = $"{candidate.FileName}: process did not start";
                return false;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                process.Kill();
                process.WaitForExit();
                outputTask.GetAwaiter().GetResult();
                errorTask.GetAwaiter().GetResult();
                result = InfrastructureFailure(
                    $"Package contract validator timed out after {ProcessTimeoutMilliseconds / 1000} seconds.");
                return true;
            }

            string output = outputTask.GetAwaiter().GetResult();
            string standardError = errorTask.GetAwaiter().GetResult();
            if (!TryParseResult(output, process.ExitCode, out result, out string parseError))
            {
                error = $"{candidate.FileName}: {parseError} {standardError}".Trim();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{candidate.FileName}: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseResult(
        string json,
        int processExitCode,
        out ActionFitPackageContractValidationResult result,
        out string error)
    {
        result = null;
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"validator returned no JSON (exit {processExitCode})";
            return false;
        }

        CliResultDto dto;
        try
        {
            dto = JsonUtility.FromJson<CliResultDto>(json);
        }
        catch (Exception ex)
        {
            error = $"validator returned invalid JSON: {ex.Message}";
            return false;
        }

        if (dto == null || dto.exitCode != processExitCode || dto.success != (processExitCode == 0))
        {
            error = $"validator JSON did not match process exit code {processExitCode}";
            return false;
        }

        ActionFitPackageContractDiagnostic[] diagnostics = (dto.diagnostics ?? Array.Empty<CliDiagnosticDto>())
            .Select(item => new ActionFitPackageContractDiagnostic
            {
                Code = item.code ?? "",
                Severity = item.severity ?? "",
                Path = item.path ?? "",
                Line = item.line,
                Message = item.message ?? "",
                SuggestedFix = item.suggestedFix ?? "",
            })
            .ToArray();
        string code = dto.success
            ? "PACKAGE_CONTRACT_VALID"
            : dto.exitCode == 1
                ? "PACKAGE_CONTRACT_INVALID"
                : "PACKAGE_CONTRACT_INFRASTRUCTURE";
        string message = dto.success
            ? "Package contract validation passed."
            : diagnostics.Length > 0
                ? $"Package contract validation failed with {diagnostics.Length} diagnostic(s). {diagnostics[0].ToSummary()}"
                : $"Package contract validation failed with exit code {dto.exitCode}.";

        result = new ActionFitPackageContractValidationResult
        {
            Success = dto.success,
            ExitCode = dto.exitCode,
            Code = code,
            Message = message,
            RawJson = json,
            Diagnostics = diagnostics,
        };
        return true;
    }

    private static string ResolvePackageRoot()
    {
        const string packageJsonPath = "Packages/com.actionfit.custompackagemanager/package.json";
        UnityEditor.PackageManager.PackageInfo package =
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath(packageJsonPath);
        string packageRoot = package?.resolvedPath;
        if (string.IsNullOrWhiteSpace(packageRoot))
            packageRoot = ActionFitPackagePaths.ProjectRelativeFullPath($"Packages/{PackageId}");
        return Path.GetFullPath(packageRoot);
    }

    private static string ResolveRepositoryRoot(string packageRoot)
    {
        DirectoryInfo packagesDirectory = Directory.GetParent(packageRoot);
        if (packagesDirectory?.Parent != null &&
            string.Equals(packagesDirectory.Name, "Packages", StringComparison.OrdinalIgnoreCase))
        {
            return packagesDirectory.Parent.FullName;
        }

        return ActionFitPackagePaths.ProjectRoot;
    }

    private static IEnumerable<InterpreterCandidate> GetInterpreterCandidates()
    {
        if (Application.platform == RuntimePlatform.WindowsEditor)
        {
            yield return new InterpreterCandidate { FileName = "python", PrefixArguments = "" };
            yield return new InterpreterCandidate { FileName = "py", PrefixArguments = "-3" };
            yield return new InterpreterCandidate { FileName = "python3", PrefixArguments = "" };
            yield break;
        }

        yield return new InterpreterCandidate { FileName = "python3", PrefixArguments = "" };
        yield return new InterpreterCandidate { FileName = "python", PrefixArguments = "" };
    }

    private static ActionFitPackageContractValidationResult InfrastructureFailure(string message)
        => ActionFitPackageContractValidationResult.Failed(
            "PACKAGE_CONTRACT_INFRASTRUCTURE",
            message,
            2);

    private static string Quote(string value)
        => $"\"{(value ?? "").Replace("\"", "\\\"")}\"";
}
#endif
