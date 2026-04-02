using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using Soenneker.Twilio.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using System.Collections.Generic;

namespace Soenneker.Twilio.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IProcessUtil _processUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IOpenApiFixer _openApiFixer;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil, IProcessUtil processUtil, 
        IFileUtil fileUtil, IDirectoryUtil directoryUtil, IOpenApiMerger openApiMerger, IOpenApiFixer openApiFixer, IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _processUtil = processUtil;
        _kiotaUtil = kiotaUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiMerger = openApiMerger;
        _openApiFixer = openApiFixer;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string openApiFilePath = Path.Combine(gitDirectory, "openapi.json");
        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");

        await _fileUtil.DeleteIfExists(openApiFilePath, cancellationToken: cancellationToken);
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);

        string openApiSourceUrl = _configuration["Twilio:OpenApiGitUrl"] ??
                                  _configuration["Twilio:ClientGenerationUrl"] ??
                                  "https://github.com/twilio/twilio-oai/blob/main/spec/json/";

        (string openApiGitUrl, string? repositorySubdirectory) = ResolveGitRepositorySource(openApiSourceUrl);

        string openApiRepositoryDirectory = await _gitUtil.CloneToTempDirectory(openApiGitUrl, cancellationToken: cancellationToken);

        string openApiDirectory = openApiRepositoryDirectory;

        if (!repositorySubdirectory.IsNullOrWhiteSpace())
        {
            openApiDirectory = Path.Combine(openApiRepositoryDirectory, repositorySubdirectory!);

            if (!await _directoryUtil.Exists(openApiDirectory, cancellationToken))
                throw new DirectoryNotFoundException($"OpenAPI repository subdirectory was not found: {openApiDirectory}");
        }

        _logger.LogInformation("Merging Twilio OpenAPI specs from {OpenApiGitUrl} ({RepositorySubdirectory})...", openApiGitUrl,
            repositorySubdirectory ?? "/");

        OpenApiDocument mergedOpenApiDocument = await _openApiMerger.MergeDirectory(openApiDirectory, cancellationToken);
        string mergedOpenApiJson = _openApiMerger.ToJson(mergedOpenApiDocument);

        await _fileUtil.Write(openApiFilePath, mergedOpenApiJson, true, cancellationToken);
        await _openApiFixer.Fix(openApiFilePath, fixedFilePath, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "TwilioOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private static (string gitUrl, string? repositorySubdirectory) ResolveGitRepositorySource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 2)
            {
                string gitUrl = $"https://github.com/{segments[0]}/{segments[1]}";

                if (segments.Length >= 5 &&
                    (segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase)))
                {
                    string repositorySubdirectory = Path.Combine(segments.Skip(4).ToArray());
                    return (gitUrl, repositorySubdirectory);
                }

                return (gitUrl, null);
            }
        }

        return (source, null);
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, "Jake Soenneker", "jake@soenneker.com", cancellationToken);
    }
}
