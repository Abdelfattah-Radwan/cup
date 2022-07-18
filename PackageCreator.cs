﻿using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace CreateUnityPackage;

internal static class PackageCreator
{
	private static readonly EnumerationOptions FileSystemEnumerationOptions = new()
	{
		RecurseSubdirectories = false,
		ReturnSpecialDirectories = false,
	};

	private static readonly Regex MetaGuidRegex = new(@"guid:\s*(?<guid>\S+)", RegexOptions.Compiled);

	private static readonly Regex AssetsSubdirectoryRegex = new(@"Assets(\\|\/)(.*)", RegexOptions.Compiled);

	private static async Task<Asset?> FindAsset(string filePath)
	{
		string metaFilePath = $"{filePath}.meta";

		if (!File.Exists(metaFilePath)) return null;

		using StreamReader reader = File.OpenText(metaFilePath);

		while (await reader.ReadLineAsync() is { } line && !string.IsNullOrWhiteSpace(line))
		{
			if (MetaGuidRegex.Match(line) is not { Success: true } match) continue;

			string guid = match.Groups["guid"].Value;

			return string.IsNullOrWhiteSpace(guid) ? null : new Asset(filePath, metaFilePath, guid);
		}

		return null;
	}

	private static async Task<ImmutableArray<Asset>> FindAssets(string sourceDirectoryPath, IReadOnlySet<string> ignoredPaths)
	{
		ImmutableArray<Asset>.Builder assets = ImmutableArray.CreateBuilder<Asset>();

		foreach (string path in Directory.EnumerateFileSystemEntries(sourceDirectoryPath, "*", FileSystemEnumerationOptions))
		{
			if (ignoredPaths.Contains(path)) continue;

			if (Directory.Exists(path))
			{
				assets.AddRange(await FindAssets(path, ignoredPaths));
			}
			else if (File.Exists(path))
			{
				if (await FindAsset(path) is { } asset) assets.Add(asset);
			}
			else
			{
				throw new Exception($"Unable to deduce the path type. {nameof(path)}: '{path}'");
			}
		}

		return assets.ToImmutable();
	}

	public static async Task CreatePackageFromDirectory(Options options)
	{
		HashSet<string> ignoredPaths = options.IgnoredPaths.Select(Path.GetFullPath).ToHashSet();

		ImmutableArray<Asset> assets = await FindAssets(Environment.CurrentDirectory, ignoredPaths);

		if (assets.Length == 0) return;

		string temporaryDirectoryPath = Path.Combine(Path.GetTempPath(), $"cup-{Guid.NewGuid():N}");

		try
		{
			foreach (Asset asset in assets)
			{
				DirectoryInfo assetDirectory = Directory.CreateDirectory(Path.Combine(temporaryDirectoryPath, asset.Guid));

				File.Copy(asset.FilePath, Path.Combine(assetDirectory.FullName, "asset"));

				File.Copy(asset.MetaFilePath, Path.Combine(assetDirectory.FullName, "asset.meta"));

				await File.WriteAllTextAsync(Path.Combine(assetDirectory.FullName, "pathname"), AssetsSubdirectoryRegex.Match(asset.FilePath).Value);
			}

			if (File.Exists(options.OutputFilePath)) File.Delete(options.OutputFilePath);

			ZipFile.CreateFromDirectory(temporaryDirectoryPath, options.OutputFilePath, (CompressionLevel)options.Compression, false);
		}
		finally
		{
			if (Directory.Exists(temporaryDirectoryPath)) Directory.Delete(temporaryDirectoryPath, true);
		}
	}
}
