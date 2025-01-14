// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using Microsoft.Health.Dicom.Core.Extensions;
using Microsoft.Health.Dicom.Core.Features.ChangeFeed;
using Microsoft.Health.Dicom.Core.Features.Model;
using Microsoft.Health.Dicom.Core.Features.Partition;
using Microsoft.Health.Dicom.Core.Models;
using Microsoft.Health.Dicom.Tests.Common;
using Microsoft.Health.Dicom.Tests.Common.Extensions;
using Microsoft.Health.Dicom.Tests.Integration.Persistence.Models;
using Xunit;

namespace Microsoft.Health.Dicom.Tests.Integration.Persistence;

[Collection("Change Feed Collection")]
public class ChangeFeedTests : IClassFixture<ChangeFeedTestsFixture>
{
    private readonly ChangeFeedTestsFixture _fixture;

    public ChangeFeedTests(ChangeFeedTestsFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GivenInstance_WhenAddedAndDeletedAndAdded_ChangeFeedEntryAvailable()
    {
        // create and validate
        var dicomInstanceIdentifier = await CreateInstanceAsync();
        await ValidateInsertFeedAsync(dicomInstanceIdentifier, 1);

        // delete and validate
        await _fixture.DicomIndexDataStore.DeleteInstanceIndexAsync(DefaultPartition.Key, dicomInstanceIdentifier.StudyInstanceUid, dicomInstanceIdentifier.SeriesInstanceUid, dicomInstanceIdentifier.SopInstanceUid, DateTime.Now, CancellationToken.None);
        await ValidateDeleteFeedAsync(dicomInstanceIdentifier, 2);

        // re-create the same instance and validate
        await CreateInstanceAsync(true, dicomInstanceIdentifier.StudyInstanceUid, dicomInstanceIdentifier.SeriesInstanceUid, dicomInstanceIdentifier.SopInstanceUid);
        await ValidateInsertFeedAsync(dicomInstanceIdentifier, 3);
    }

    [Fact]
    public async Task GivenCreatingInstance_WhenDeleted_ValidateNoChangeFeedRecord()
    {
        // create and validate
        var dicomInstanceIdentifier = await CreateInstanceAsync(instanceFullyCreated: false);
        await ValidateNoChangeFeedAsync(dicomInstanceIdentifier);

        // delete and validate
        await _fixture.DicomIndexDataStore.DeleteInstanceIndexAsync(DefaultPartition.Key, dicomInstanceIdentifier.StudyInstanceUid, dicomInstanceIdentifier.SeriesInstanceUid, dicomInstanceIdentifier.SopInstanceUid, DateTime.Now, CancellationToken.None);
        await ValidateNoChangeFeedAsync(dicomInstanceIdentifier);
    }

    [Fact]
    public async Task GivenRecords_WhenQueryWithWindows_ThenScopeResults()
    {
        // Insert data over time
        DateTimeOffset start = DateTimeOffset.UtcNow;
        VersionedInstanceIdentifier instance1 = await CreateInstanceAsync();
        await Task.Delay(1000);
        VersionedInstanceIdentifier instance2 = await CreateInstanceAsync();
        VersionedInstanceIdentifier instance3 = await CreateInstanceAsync();

        // Get all creation events
        var testRange = new TimeRange(start.AddMilliseconds(-1), DateTimeOffset.UtcNow.AddMilliseconds(1));
        IReadOnlyList<ChangeFeedEntry> changes = await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(testRange, 0, 10, ChangeFeedOrder.Time);
        Assert.Equal(3, changes.Count);
        Assert.Equal(instance1.Version, changes[0].CurrentVersion);
        Assert.Equal(instance2.Version, changes[1].CurrentVersion);
        Assert.Equal(instance3.Version, changes[2].CurrentVersion);

        // Fetch changes outside of the range
        IReadOnlyList<ChangeFeedEntry> existingEvents = await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(TimeRange.Before(changes[0].Timestamp), 0, 100, ChangeFeedOrder.Time);
        Assert.DoesNotContain(existingEvents, x => changes.Any(y => y.Sequence == x.Sequence));

        Assert.Empty(await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(TimeRange.After(changes[1].Timestamp), 2, 100, ChangeFeedOrder.Time));
        Assert.Empty(await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(TimeRange.After(changes[2].Timestamp.AddMilliseconds(1)), 0, 100, ChangeFeedOrder.Time));

        // Fetch changes limited to window
        await ValidateSubsetAsync(testRange, changes[0], changes[1], changes[2]);
        await ValidateSubsetAsync(new TimeRange(changes[0].Timestamp, changes[2].Timestamp), changes[0], changes[1]);
    }

    private async Task ValidateInsertFeedAsync(VersionedInstanceIdentifier dicomInstanceIdentifier, int expectedCount)
    {
        IReadOnlyList<ChangeFeedRow> result = await _fixture.DicomIndexDataStoreTestHelper.GetChangeFeedRowsAsync(
            dicomInstanceIdentifier.StudyInstanceUid,
            dicomInstanceIdentifier.SeriesInstanceUid,
            dicomInstanceIdentifier.SopInstanceUid);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.Count);
        Assert.Equal((int)ChangeFeedAction.Create, result.Last().Action);
        Assert.Equal(result.Last().OriginalWatermark, result.Last().CurrentWatermark);

        int i = 0;
        while (i < expectedCount - 1)
        {
            ChangeFeedRow r = result[i];
            Assert.NotEqual(r.OriginalWatermark, r.CurrentWatermark);
            i++;
        }
    }

    private async Task ValidateDeleteFeedAsync(VersionedInstanceIdentifier dicomInstanceIdentifier, int expectedCount)
    {
        IReadOnlyList<ChangeFeedRow> result = await _fixture.DicomIndexDataStoreTestHelper.GetChangeFeedRowsAsync(
            dicomInstanceIdentifier.StudyInstanceUid,
            dicomInstanceIdentifier.SeriesInstanceUid,
            dicomInstanceIdentifier.SopInstanceUid);

        Assert.NotNull(result);
        Assert.Equal(expectedCount, result.Count);
        Assert.Equal((int)ChangeFeedAction.Delete, result.Last().Action);

        foreach (ChangeFeedRow row in result)
        {
            Assert.Null(row.CurrentWatermark);
        }
    }

    private async Task ValidateNoChangeFeedAsync(VersionedInstanceIdentifier dicomInstanceIdentifier)
    {
        IReadOnlyList<ChangeFeedRow> result = await _fixture.DicomIndexDataStoreTestHelper.GetChangeFeedRowsAsync(
            dicomInstanceIdentifier.StudyInstanceUid,
            dicomInstanceIdentifier.SeriesInstanceUid,
            dicomInstanceIdentifier.SopInstanceUid);

        Assert.NotNull(result);
        Assert.Equal(0, result.Count);
    }

    private async Task ValidateSubsetAsync(TimeRange range, params ChangeFeedEntry[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            IReadOnlyList<ChangeFeedEntry> changes = await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(range, i, 1, ChangeFeedOrder.Time);

            Assert.Single(changes);
            Assert.Equal(expected[i].Sequence, changes.Single().Sequence);
        }

        Assert.Empty(await _fixture.DicomChangeFeedStore.GetChangeFeedAsync(range, expected.Length, 1, ChangeFeedOrder.Time));
    }

    private async Task<VersionedInstanceIdentifier> CreateInstanceAsync(
        bool instanceFullyCreated = true,
        string studyInstanceUid = null,
        string seriesInstanceUid = null,
        string sopInstanceUid = null)
    {
        var newDataSet = new DicomDataset()
        {
            { DicomTag.StudyInstanceUID, studyInstanceUid ?? TestUidGenerator.Generate() },
            { DicomTag.SeriesInstanceUID, seriesInstanceUid ?? TestUidGenerator.Generate() },
            { DicomTag.SOPInstanceUID, sopInstanceUid ?? TestUidGenerator.Generate() },
            { DicomTag.PatientID, TestUidGenerator.Generate() },
        };

        var version = await _fixture.DicomIndexDataStore.BeginCreateInstanceIndexAsync(1, newDataSet);

        var versionedIdentifier = newDataSet.ToVersionedInstanceIdentifier(version);

        if (instanceFullyCreated)
        {
            await _fixture.DicomIndexDataStore.EndCreateInstanceIndexAsync(1, newDataSet, version);
        }

        return versionedIdentifier;
    }
}
