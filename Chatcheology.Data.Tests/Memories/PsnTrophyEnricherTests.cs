using System.Security.Cryptography;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    public class PsnTrophyEnricherTests
    {
        /// <summary>Imports the standard PSN file into a new memories file (format 2, or format 1 as step 1 made it).</summary>
        internal static string ImportStandard(MemoriesTestFolder folder, bool formatOne = false)
        {
            if (formatOne)
            {
                MemoriesDatabase.CreateFormatVersionOne(folder.MemoriesPath, MemoriesTestFolder.Now);
            }
            else
            {
                folder.CreateMemories();
            }

            var file = Standard();
            new PsnImporter().Import(folder.MemoriesPath, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));
            SqliteConnection.ClearAllPools();
            return folder.MemoriesPath;
        }

        private static PsnTrophyDefinitions Definitions(SyntheticTrophyDefinitions? file = null) =>
            PsnTrophyDefinitionsReader.Parse((file ?? SyntheticTrophyDefinitions.Standard()).ToBytes());

        /// <summary>Every row of every step 1 table, as text, so a before/after comparison sees any change.</summary>
        private static List<string> DumpVersionOneRows(MemoriesTestFolder folder)
        {
            var rows = new List<string>();

            foreach (var table in new[] { "MediaFile", "Collection", "Entry", "EntryPeriod", "EntryItem", "EntryLink" })
            {
                using var connection = new SqliteConnection($"Data Source={folder.MemoriesPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table} ORDER BY 1, 2;";
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Where(i => reader.GetName(i) != "EveryYear") // CH-53: format 6 adds it; checked on its own
                        .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    rows.Add(table + "|" + string.Join("|", values));
                }
            }

            SqliteConnection.ClearAllPools();
            return rows;
        }

        [Fact]
        public void Enrich_StoresDefinitions_AndCountsItems()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);

            var result = PsnTrophyEnricher.Enrich(path, Definitions());

            Assert.Equal(new PsnEnrichResult(MemoriesDatabase.CurrentFormatVersion, false, 2, 4, 1, 5, 1, 0, 0, 4, 0, 0, 0), result);
            Assert.Equal(4, result.Changes);
            Assert.Equal(4, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition;"));
            Assert.Equal(
                ["Invented First Step|Invented detail.|0|default|" + SyntheticTrophyDefinitions.QuestIconOne + "|<null>"],
                folder.Strings(
                    "SELECT Name || '|' || Detail || '|' || Hidden || '|' || GroupId || '|' || IconSourceUrl || '|' || ifnull(IconSha256, '<null>') " +
                    "FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '1';"));
            Assert.Equal(["<null>|<null>|1"], folder.Strings(
                "SELECT ifnull(Name, '<null>') || '|' || ifnull(Detail, '<null>') || '|' || Hidden FROM ItemDefinition WHERE ItemId = '3';"));

            // Entry items are never written: their Name and IconSha256 stay null.
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM EntryItem WHERE Name IS NOT NULL OR IconSha256 IS NOT NULL;"));
        }

        [Fact]
        public void ReRun_ReportsZeroChanges()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            PsnTrophyEnricher.Enrich(path, Definitions());

            var result = PsnTrophyEnricher.Enrich(path, Definitions());

            Assert.Equal(0, result.Changes);
            Assert.Equal(4, result.Unchanged);
            Assert.Equal(5, result.ItemsWithDefinition);
        }

        [Fact]
        public void ChangedName_UpdatesThatDefinitionOnly_AndKeepsTheIconHashWhileTheUrlIsUnchanged()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            PsnTrophyEnricher.Enrich(path, Definitions());
            MemoryImageFetcher.FetchMissing(path, new MemoryImageFetcherTests.FakeSource(new()
            {
                [SyntheticTrophyDefinitions.QuestIconOne] = MemoryImageFetcherTests.Png,
            }));
            var changed = SyntheticTrophyDefinitions.Standard();
            changed.Sets[0].Trophies[0] = changed.Sets[0].Trophies[0] with { Name = "Invented Renamed Step" };

            var result = PsnTrophyEnricher.Enrich(path, Definitions(changed));

            Assert.Equal(1, result.Updated);
            Assert.Equal(3, result.Unchanged);
            Assert.Equal(1, result.Changes);
            Assert.Equal(["Invented Renamed Step"], folder.Strings("SELECT Name FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '1';"));
            Assert.Equal([Convert.ToHexStringLower(SHA256.HashData(MemoryImageFetcherTests.Png))],
                folder.Strings("SELECT IconSha256 FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '1';"));
        }

        [Fact]
        public void ChangedIconUrl_ClearsTheIconHash()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            PsnTrophyEnricher.Enrich(path, Definitions());
            MemoryImageFetcher.FetchMissing(path, new MemoryImageFetcherTests.FakeSource(new()
            {
                [SyntheticTrophyDefinitions.QuestIconOne] = MemoryImageFetcherTests.Png,
            }));
            var changed = SyntheticTrophyDefinitions.Standard();
            changed.Sets[0].Trophies[0] = changed.Sets[0].Trophies[0] with { Icon = "https://image.api.playstation.com/zz/questtrophy1b.png" };

            Assert.Equal(1, PsnTrophyEnricher.Enrich(path, Definitions(changed)).Updated);
            Assert.Equal(["<null>"], folder.Strings("SELECT IconSha256 FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '1';"));
        }

        [Fact]
        public void TypeMismatch_IsNotStored_AndIsCounted()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets[0].Trophies[1] = file.Sets[0].Trophies[1] with { Type = "gold" };

            var result = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(1, result.TypeMismatches);
            Assert.Equal(2, result.ItemsTypeMismatch);
            Assert.Equal(3, result.ItemsWithDefinition);
            Assert.Equal(1, result.ItemsWithoutDefinition);
            Assert.Equal(3, result.Added);
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '2';"));
        }

        [Fact]
        public void TypeComparison_IgnoresCaseOnly()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets[0].Trophies[1] = file.Sets[0].Trophies[1] with { Type = "Silver" };

            var result = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(0, result.TypeMismatches);
            Assert.Equal(["Silver"], folder.Strings("SELECT ItemType FROM ItemDefinition WHERE SetId = 'NPWR99999_00' AND ItemId = '2';"));
        }

        [Fact]
        public void StoredDefinitionThatNowMismatches_IsRemovedAndCounted()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            PsnTrophyEnricher.Enrich(path, Definitions());
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets[1].Trophies[0] = file.Sets[1].Trophies[0] with { Type = "platinum" };

            var result = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(1, result.RemovedForMismatch);
            Assert.Equal(1, result.Changes);
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition WHERE SetId = 'NPWR88888_00';"));
        }

        [Fact]
        public void StoredDefinitionNotInTheFile_ThatNowMismatchesAReImportedItem_IsRemovedAndCounted()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            PsnTrophyEnricher.Enrich(path, Definitions());

            // Re-import with the Racer local item (set NPWR88888_00, trophy 5) now gold.
            var psn = Standard();
            psn.Events[1].Local[0] = psn.Events[1].Local[0] with { Type = "gold" };
            Assert.Equal(1, new PsnImporter().Import(path, PsnSharedActivityReader.Parse(psn.ToBytes(), LocalPrefix, OtherPrefix, psn.PairAPrefix)).Updated);

            // The next definitions file does not carry the Racer set at all.
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets.RemoveAt(1);

            var dry = PsnTrophyEnricher.DryRun(path, Definitions(file));
            var result = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(dry, result);
            Assert.Equal(1, result.RemovedForMismatch);
            Assert.Equal(1, result.TypeMismatches);
            Assert.Equal(1, result.ItemsTypeMismatch);
            Assert.Equal(4, result.ItemsWithDefinition);
            Assert.Equal(1, result.ItemsWithoutDefinition);
            Assert.Equal(1, result.Changes);
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition WHERE SetId = 'NPWR88888_00';"));

            var again = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(0, again.Changes);
            Assert.Equal(0, again.ItemsTypeMismatch);
            Assert.Equal(2, again.ItemsWithoutDefinition);
        }

        [Fact]
        public void ItemsWithoutADefinition_AreCounted()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets.RemoveAt(1);

            var result = PsnTrophyEnricher.Enrich(path, Definitions(file));

            Assert.Equal(4, result.ItemsWithDefinition);
            Assert.Equal(2, result.ItemsWithoutDefinition);
        }

        [Fact]
        public void DryRun_CountsTheSame_AndWritesNothing()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder);
            var before = File.ReadAllBytes(path);

            var dry = PsnTrophyEnricher.DryRun(path, Definitions());

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(PsnTrophyEnricher.Enrich(path, Definitions()), dry);
        }

        [Fact]
        public void FormatOneFile_IsUpgradedInTheSameRun_KeepingEveryRow()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder, formatOne: true);
            var rowsBefore = DumpVersionOneRows(folder);
            Assert.Equal(1, folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ItemDefinition';"));
            var createdBefore = folder.Strings("SELECT CreatedDateTimeUtc FROM MemoriesInfo;");

            var dry = PsnTrophyEnricher.DryRun(path, Definitions());
            Assert.True(dry.Upgraded);
            Assert.Equal(1, folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));

            var result = PsnTrophyEnricher.Enrich(path, Definitions());

            Assert.Equal(1, result.FormatVersionBefore);
            Assert.True(result.Upgraded);
            Assert.Equal(5, result.Changes);
            Assert.Equal(dry, result);
            Assert.Equal(rowsBefore, DumpVersionOneRows(folder));
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(createdBefore, folder.Strings("SELECT CreatedDateTimeUtc FROM MemoriesInfo;"));
            Assert.Equal(4, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition;"));

            var again = PsnTrophyEnricher.Enrich(path, Definitions());

            Assert.False(again.Upgraded);
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, again.FormatVersionBefore);
            Assert.Equal(0, again.Changes);
            Assert.Equal(rowsBefore, DumpVersionOneRows(folder));
        }

        [Fact]
        public void UpgradedFile_HasTheSameLayoutAsANewOne()
        {
            using var upgraded = new MemoriesTestFolder();
            using var created = new MemoriesTestFolder();
            PsnTrophyEnricher.Enrich(ImportStandard(upgraded, formatOne: true), Definitions());
            created.CreateMemories();

            const string Layout = "SELECT type || ' ' || name || ' ' || ifnull(sql, '') FROM sqlite_master ORDER BY type, name;";

            Assert.Equal(created.Strings(Layout), upgraded.Strings(Layout));
        }

        [Fact]
        public void FailedEnrichment_RollsBackTheUpgrade()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder, formatOne: true);

            // A definition whose type is empty cannot be read, so build one past the reader.
            var definitions = new PsnTrophyDefinitions(1, [new PsnTrophyDefinition("NPWR11111_00", "1", string.Empty, null, null, null, null, null)]);

            Assert.Throws<SqliteException>(() => PsnTrophyEnricher.Enrich(path, definitions));
            SqliteConnection.ClearAllPools();
            Assert.Equal(1, folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ItemDefinition';"));
        }

        [Fact]
        public void PsnReImport_AfterEnrichment_LeavesDefinitionsAlone()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportStandard(folder, formatOne: true);
            PsnTrophyEnricher.Enrich(path, Definitions());
            var file = Standard();

            var import = new PsnImporter().Import(path, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));

            Assert.Equal(0, import.Changes);
            Assert.Equal(4, folder.Scalar("SELECT COUNT(*) FROM ItemDefinition;"));
        }
    }
}
