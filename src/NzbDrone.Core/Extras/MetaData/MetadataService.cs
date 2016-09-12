﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Extras.ExtraFiles;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras.Metadata
{
    public interface IMetadataService
    {
        List<ExtraFile> CreateSeasonAndSeriesMetadata(Series series, List<EpisodeFile> episodeFiles, List<ExtraFile> metadataFiles);
        List<ExtraFile> CreateEpisodeMetadata(Series series, EpisodeFile episodeFile);
        List<ExtraFile> CreateSeasonAndSeriesMetadataAfterEpisodeImport(Series series, string seriesFolder, string seasonFolder, List<ExtraFile> metadataFiles);
        List<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles, List<ExtraFile> metadataFiles);
    }

    public class MetadataService : IMetadataService
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly ICleanMetadataService _cleanMetadataService;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IHttpClient _httpClient;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly IExtraFileService _extraFileService;
        private readonly Logger _logger;

        public MetadataService(IMetadataFactory metadataFactory,
                               ICleanMetadataService cleanMetadataService,
                               IDiskTransferService diskTransferService,
                               IDiskProvider diskProvider,
                               IHttpClient httpClient,
                               IMediaFileAttributeService mediaFileAttributeService,
                               IExtraFileService extraFileService,
                               Logger logger)
        {
            _metadataFactory = metadataFactory;
            _cleanMetadataService = cleanMetadataService;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _httpClient = httpClient;
            _mediaFileAttributeService = mediaFileAttributeService;
            _extraFileService = extraFileService;
            _logger = logger;
        }

        public List<ExtraFile> CreateSeasonAndSeriesMetadata(Series series, List<EpisodeFile> episodeFiles, List<ExtraFile> metadataFiles)
        {
            _cleanMetadataService.Clean(series);

            if (!_diskProvider.FolderExists(series.Path))
            {
                _logger.Info("Series folder does not exist, skipping metadata creation");
                return new List<ExtraFile>();
            }
            
            var files = new List<ExtraFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                files.AddIfNotNull(ProcessSeriesMetadata(consumer, series, consumerFiles));
                files.AddRange(ProcessSeriesImages(consumer, series, consumerFiles));
                files.AddRange(ProcessSeasonImages(consumer, series, consumerFiles));

                foreach (var episodeFile in episodeFiles)
                {
                    files.AddIfNotNull(ProcessEpisodeMetadata(consumer, series, episodeFile, consumerFiles));
                    files.AddRange(ProcessEpisodeImages(consumer, series, episodeFile, consumerFiles));
                }
            }

            return files;
        }

        public List<ExtraFile> CreateEpisodeMetadata(Series series, EpisodeFile episodeFile)
        {
            var files = new List<ExtraFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {

                files.AddIfNotNull(ProcessEpisodeMetadata(consumer, series, episodeFile, new List<ExtraFile>()));
                files.AddRange(ProcessEpisodeImages(consumer, series, episodeFile, new List<ExtraFile>()));
            }

            return files;
        }

        public List<ExtraFile> CreateSeasonAndSeriesMetadataAfterEpisodeImport(Series series, string seriesFolder, string seasonFolder, List<ExtraFile> metadataFiles)
        {
            if (seriesFolder.IsNullOrWhiteSpace() && seasonFolder.IsNullOrWhiteSpace())
            {
                return new List<ExtraFile>();
            }

            var files = new List<ExtraFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                if (seriesFolder.IsNotNullOrWhiteSpace())
                {
                    files.AddIfNotNull(ProcessSeriesMetadata(consumer, series, consumerFiles));
                    files.AddRange(ProcessSeriesImages(consumer, series, consumerFiles));
                }

                if (seasonFolder.IsNotNullOrWhiteSpace())
                {
                    files.AddRange(ProcessSeasonImages(consumer, series, consumerFiles));
                }
            }

            return files;
        }

        public List<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles, List<ExtraFile> metadataFiles)
        {
            var movedFiles = new List<ExtraFile>();

            // TODO: Move EpisodeImage and EpisodeMetadata metadata files, instead of relying on consumers to do it
            // (Xbmc's EpisodeImage is more than just the extension)

            foreach (var consumer in _metadataFactory.GetAvailableProviders())
            {
                foreach (var episodeFile in episodeFiles)
                {
                    var metadataFilesForConsumer = GetMetadataFilesForConsumer(consumer, metadataFiles).Where(m => m.EpisodeFileId == episodeFile.Id).ToList();

                    foreach (var metadataFile in metadataFilesForConsumer)
                    {
                        var newFileName = consumer.GetFilenameAfterMove(series, episodeFile, metadataFile);
                        var existingFileName = Path.Combine(series.Path, metadataFile.RelativePath);

                        if (!newFileName.PathEquals(existingFileName))
                        {
                            try
                            {
                                _diskProvider.MoveFile(existingFileName, newFileName);
                                metadataFile.RelativePath = series.Path.GetRelativePath(newFileName);
                                movedFiles.Add(metadataFile);
                            }
                            catch (Exception ex)
                            {
                                _logger.WarnException("Unable to move metadata file: " + existingFileName, ex);
                            }
                        }
                    }
                }
            }

            return movedFiles;
        }

        private List<ExtraFile> GetMetadataFilesForConsumer(IMetadata consumer, List<ExtraFile> seriesMetadata)
        {
            return seriesMetadata.Where(c => c.MetadataConsumer == consumer.GetType().Name).ToList();
        }

        private ExtraFile ProcessSeriesMetadata(IMetadata consumer, Series series, List<ExtraFile> existingMetadataFiles)
        {
            var seriesMetadata = consumer.SeriesMetadata(series);

            if (seriesMetadata == null)
            {
                return null;
            }

            var hash = seriesMetadata.Contents.SHA256Hash();

            var metadata = GetMetadataFile(series, existingMetadataFiles, e => e.MetadataType == MetadataType.SeriesMetadata) ??
                               new ExtraFile
                               {
                                   Type = ExtraType.Metadata,
                                   SeriesId = series.Id,
                                   MetadataConsumer = consumer.GetType().Name,
                                   Type = MetadataType.SeriesMetadata
                               };

            if (hash == metadata.Hash)
            {
                if (seriesMetadata.RelativePath != metadata.RelativePath)
                {
                    metadata.RelativePath = seriesMetadata.RelativePath;

                    return metadata;
                }

                return null;
            }

            var fullPath = Path.Combine(series.Path, seriesMetadata.RelativePath);

            _logger.Debug("Writing Series Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, seriesMetadata.Contents);

            metadata.Hash = hash;
            metadata.RelativePath = seriesMetadata.RelativePath;

            return metadata;
        }

        private ExtraFile ProcessEpisodeMetadata(IMetadata consumer, Series series, EpisodeFile episodeFile, List<ExtraFile> existingMetadataFiles)
        {
            var episodeMetadata = consumer.EpisodeMetadata(series, episodeFile);

            if (episodeMetadata == null)
            {
                return null;
            }

            var fullPath = Path.Combine(series.Path, episodeMetadata.RelativePath);

            var existingMetadata = GetMetadataFile(series, existingMetadataFiles, c => c.MetadataType == MetadataType.EpisodeMetadata &&
                                                                                  c.EpisodeFileId == episodeFile.Id);

            if (existingMetadata != null)
            {
                var existingFullPath = Path.Combine(series.Path, existingMetadata.RelativePath);
                if (!fullPath.PathEquals(existingFullPath))
                {
                    _diskTransferService.TransferFile(existingFullPath, fullPath, TransferMode.Move);
                    existingMetadata.RelativePath = episodeMetadata.RelativePath;
                }
            }

            var hash = episodeMetadata.Contents.SHA256Hash();

            var metadata = existingMetadata ??
                           new ExtraFile
                           {
                               Type = ExtraType.Metadata,
                               SeriesId = series.Id,
                               SeasonNumber = episodeFile.SeasonNumber,
                               EpisodeFileId = episodeFile.Id,
                               MetadataConsumer = consumer.GetType().Name,
                               MetadataType = MetadataType.EpisodeMetadata,
                               RelativePath = episodeMetadata.RelativePath
                           };

            if (hash == metadata.Hash)
            {
                return null;
            }

            _logger.Debug("Writing Episode Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, episodeMetadata.Contents);

            metadata.Hash = hash;

            return metadata;
        }

        private List<ExtraFile> ProcessSeriesImages(IMetadata consumer, Series series, List<ExtraFile> existingMetadataFiles)
        {
            var result = new List<ExtraFile>();

            foreach (var image in consumer.SeriesImages(series))
            {
                var fullPath = Path.Combine(series.Path, image.RelativePath);

                if (_diskProvider.FileExists(fullPath))
                {
                    _logger.Debug("Series image already exists: {0}", fullPath);
                    continue;
                }

                var metadata = GetMetadataFile(series, existingMetadataFiles, c => c.MetadataType == MetadataType.SeriesImage &&
                                                                              c.RelativePath == image.RelativePath) ??
                               new ExtraFile
                               {
                                   Type = ExtraType.Metadata,
                                   SeriesId = series.Id,
                                   MetadataConsumer = consumer.GetType().Name,
                                   MetadataType = MetadataType.SeriesImage,
                                   RelativePath = image.RelativePath
                               };

                DownloadImage(series, image);

                result.Add(metadata);
            }

            return result;
        }

        private List<ExtraFile> ProcessSeasonImages(IMetadata consumer, Series series, List<ExtraFile> existingMetadataFiles)
        {
            var result = new List<ExtraFile>();

            foreach (var season in series.Seasons)
            {
                foreach (var image in consumer.SeasonImages(series, season))
                {
                    var fullPath = Path.Combine(series.Path, image.RelativePath);

                    if (_diskProvider.FileExists(fullPath))
                    {
                        _logger.Debug("Season image already exists: {0}", fullPath);
                        continue;
                    }

                    var metadata = GetMetadataFile(series, existingMetadataFiles, c => c.MetadataType == MetadataType.SeasonImage &&
                                                                                  c.SeasonNumber == season.SeasonNumber &&
                                                                                  c.RelativePath == image.RelativePath) ??
                                new ExtraFile
                                {
                                    Type = ExtraType.Metadata,
                                    SeriesId = series.Id,
                                    SeasonNumber = season.SeasonNumber,
                                    MetadataConsumer = consumer.GetType().Name,
                                    MetadataType = MetadataType.SeasonImage,
                                    RelativePath = image.RelativePath
                                };

                    DownloadImage(series, image);

                    result.Add(metadata);
                }
            }

            return result;
        }

        private List<ExtraFile> ProcessEpisodeImages(IMetadata consumer, Series series, EpisodeFile episodeFile, List<ExtraFile> existingMetadataFiles)
        {
            var result = new List<ExtraFile>();

            foreach (var image in consumer.EpisodeImages(series, episodeFile))
            {
                var fullPath = Path.Combine(series.Path, image.RelativePath);

                if (_diskProvider.FileExists(fullPath))
                {
                    _logger.Debug("Episode image already exists: {0}", fullPath);
                    continue;
                }

                var existingMetadata = GetMetadataFile(series, existingMetadataFiles, c => c.MetadataType == MetadataType.EpisodeImage &&
                                                                                      c.EpisodeFileId == episodeFile.Id);

                if (existingMetadata != null)
                {
                    var existingFullPath = Path.Combine(series.Path, existingMetadata.RelativePath);
                    if (!fullPath.PathEquals(existingFullPath))
                    {
                        _diskTransferService.TransferFile(existingFullPath, fullPath, TransferMode.Move);
                        existingMetadata.RelativePath = image.RelativePath;

                        return new List<ExtraFile>{ existingMetadata };
                    }
                }

                var metadata = existingMetadata ??
                               new ExtraFile
                               {
                                   Type = ExtraType.Metadata,
                                   SeriesId = series.Id,
                                   SeasonNumber = episodeFile.SeasonNumber,
                                   EpisodeFileId = episodeFile.Id,
                                   MetadataConsumer = consumer.GetType().Name,
                                   MetadataType = MetadataType.EpisodeImage,
                                   RelativePath = image.RelativePath
                               };

                DownloadImage(series, image);

                result.Add(metadata);
            }

            return result;
        }

        private void DownloadImage(Series series, ImageFileResult image)
        {
            var fullPath = Path.Combine(series.Path, image.RelativePath);

            try
            {
                if (image.Url.StartsWith("http"))
                {
                    _httpClient.DownloadFile(image.Url, fullPath);
                }
                else
                {
                    _diskProvider.CopyFile(image.Url, fullPath);
                }
                _mediaFileAttributeService.SetFilePermissions(fullPath);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex, "Couldn't download image {0} for {1}. {2}", image.Url, series, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't download image {0} for {1}. {2}", image.Url, series, ex.Message);
            }
        }

        private void SaveMetadataFile(string path, string contents)
        {
            _diskProvider.WriteAllText(path, contents);
            _mediaFileAttributeService.SetFilePermissions(path);
        }

        private ExtraFile GetMetadataFile(Series series, List<ExtraFile> existingMetadataFiles, Func<ExtraFile, bool> predicate)
        {
            var matchingMetadataFiles = existingMetadataFiles.Where(predicate).ToList();

            if (matchingMetadataFiles.Empty())
            {
                return null;
            }

            //Remove duplicate metadata files from DB and disk
            foreach (var file in matchingMetadataFiles.Skip(1))
            {
                var path = Path.Combine(series.Path, file.RelativePath);

                _logger.Debug("Removing duplicate Metadata file: {0}", path);

                _diskProvider.DeleteFile(path);
                _extraFileService.Delete(file.Id);
            }

            
            return matchingMetadataFiles.First();
        }
    }
}
