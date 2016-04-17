using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Extras
{
    public abstract class ImportExistingExtraFilesBase<TExtraFile> : IImportExistingExtraFiles
        where TExtraFile : ExtraFile, new()
    {
        private readonly IExtraFileService<TExtraFile> _extraFileService;

        public ImportExistingExtraFilesBase(IExtraFileService<TExtraFile> extraFileService)
        {
            _extraFileService = extraFileService;
        }

        public abstract int Order { get; }
        public abstract IEnumerable<ExtraFile> ProcessFiles(Series series, List<string> filesOnDisk, List<string> importedFiles);

        public virtual List<string> FilterAndClean(Series series, List<string> filesOnDisk, List<string> importedFiles)
        {
            var seriesFiles = _extraFileService.GetFilesBySeries(series.Id);

            Clean(series, importedFiles, seriesFiles);

            return Filter(series, filesOnDisk, importedFiles, seriesFiles);
        }

        private List<string> Filter(Series series, List<string> filesOnDisk, List<string> importedFiles, List<TExtraFile> seriesFiles)
        {
            var filteredFiles = filesOnDisk;

            filteredFiles = filteredFiles.Except(seriesFiles.Select(f => Path.Combine(series.Path, f.RelativePath)).ToList(), PathEqualityComparer.Instance).ToList();
            return filteredFiles.Except(importedFiles, PathEqualityComparer.Instance).ToList();
        }

        private void Clean(Series series, List<string> importedFiles, List<TExtraFile> seriesFiles)
        {
            var alreadyImportedFileIds = seriesFiles.IntersectBy(f => Path.Combine(series.Path, f.RelativePath), importedFiles, i => i, PathEqualityComparer.Instance)
                .Select(f => f.Id);

            _extraFileService.DeleteMany(alreadyImportedFileIds);
        }
    }
}
