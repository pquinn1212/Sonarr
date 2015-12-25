﻿using System;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.ExtraFiles;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class DeleteBadMediaCovers : IHousekeepingTask
    {
        private readonly IExtraFileService _extraFileService;
        private readonly ISeriesService _seriesService;
        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DeleteBadMediaCovers(IExtraFileService extraFileService,
                                    ISeriesService seriesService,
                                    IDiskProvider diskProvider,
                                    IConfigService configService,
                                    Logger logger)
        {
            _extraFileService = extraFileService;
            _seriesService = seriesService;
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public void Clean()
        {
            if (!_configService.CleanupMetadataImages) return;

            var series = _seriesService.GetAllSeries();

            foreach (var show in series)
            {
                var images = _extraFileService.GetFilesBySeries(show.Id)
                    .Where(c => c.LastUpdated > new DateTime(2014, 12, 27) && c.RelativePath.EndsWith(".jpg", StringComparison.InvariantCultureIgnoreCase));

                foreach (var image in images)
                {
                    try
                    {
                        var path = Path.Combine(show.Path, image.RelativePath);
                        if (!IsValid(path))
                        {
                            _logger.Debug("Deleting invalid image file " + path);
                            DeleteMetadata(image.Id, path);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Couldn't validate image " + image.RelativePath);
                    }
                }
            }


            _configService.CleanupMetadataImages = false;
        }

        private void DeleteMetadata(int id, string path)
        {
            _extraFileService.Delete(id);
            _diskProvider.DeleteFile(path);
        }

        private bool IsValid(string path)
        {
            var buffer = new byte[10];

            using (var imageStream = _diskProvider.OpenReadStream(path))
            {
                if (imageStream.Length < buffer.Length) return false;
                imageStream.Read(buffer, 0, buffer.Length);
            }

            var text = System.Text.Encoding.Default.GetString(buffer);

            return !text.ToLowerInvariant().Contains("html");
        }
    }
}