using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras.Subtitles
{
    public class SubtitleService : ExtraFileManager<SubtitleFile>
    {
        private readonly ISubtitleFileService _subtitleFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public SubtitleService(IConfigService configService,
                               IDiskTransferService diskTransferService,
                               ISubtitleFileService subtitleFileService,
                               IDiskProvider diskProvider,
                               Logger logger)
            : base(configService, diskTransferService, subtitleFileService)
        {
            _subtitleFileService = subtitleFileService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public override int Order
        {
            get
            {
                return 1;
            }
        }

        public override IEnumerable<ExtraFile> CreateAfterSeriesScan(Series series, List<EpisodeFile> episodeFiles)
        {
            return Enumerable.Empty<SubtitleFile>();
        }

        public override IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, EpisodeFile episodeFile)
        {
            return Enumerable.Empty<SubtitleFile>();
        }

        public override IEnumerable<ExtraFile> CreateAfterEpisodeImport(Series series, string seriesFolder, string seasonFolder)
        {
            return Enumerable.Empty<SubtitleFile>();
        }

        public override IEnumerable<ExtraFile> MoveFilesAfterRename(Series series, List<EpisodeFile> episodeFiles)
        {
            var subtitleFiles = _subtitleFileService.GetFilesBySeries(series.Id);

            var movedFiles = new List<SubtitleFile>();

            foreach (var episodeFile in episodeFiles)
            {
                var extraFilesForEpisodeFile = subtitleFiles.Where(m => m.EpisodeFileId == episodeFile.Id).ToList();

                foreach (var extraFile in extraFilesForEpisodeFile)
                {
                    var existingFileName = Path.Combine(series.Path, extraFile.RelativePath);
                    var extension = GetExtension(extraFile, existingFileName);
                    var newFileName = Path.ChangeExtension(Path.Combine(series.Path, episodeFile.RelativePath), extension);

                    if (!newFileName.PathEquals(existingFileName))
                    {
                        try
                        {
                            _diskProvider.MoveFile(existingFileName, newFileName);
                            extraFile.RelativePath = series.Path.GetRelativePath(newFileName);
                            movedFiles.Add(extraFile);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "Unable to move subtitle file: {0}", existingFileName);
                        }
                    }
                }
            }

            _subtitleFileService.Upsert(movedFiles);

            return movedFiles;
        }

        public override ExtraFile Import(Series series, EpisodeFile episodeFile, string path, string extension, bool readOnly)
        {
            // Check the extension (.sub) vs checking the matching extension (en.sub)
            if (SubtitleFileExtensions.Extensions.Contains(Path.GetExtension(path)))
            {
                var subtitleFile = ImportFile(series, episodeFile, path, extension, readOnly);
                subtitleFile.Language = LanguageParser.ParseSubtitleLanguage(path);

                _subtitleFileService.Upsert(subtitleFile);

                return subtitleFile;
            }

            return null;
        }

        private string GetExtension(SubtitleFile extraFile, string existingFileName)
        {
            var fileExtension = Path.GetExtension(existingFileName);

            if (extraFile.Language == Language.Unknown)
            {
                return fileExtension.TrimStart('.');
            }

            return (IsoLanguages.Get(extraFile.Language).TwoLetterCode + fileExtension).TrimStart('.');
        }
    }
}
