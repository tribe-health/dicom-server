// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading.Tasks;
using EnsureThat;
using FellowOakDicom;
using Microsoft.Health.Dicom.Core.Features.Query;
using Microsoft.Health.Dicom.Core.Features.Store;
using Microsoft.Health.Dicom.Tests.Common;
using Microsoft.Health.Dicom.Tests.Common.Extensions;
using Xunit;

namespace Microsoft.Health.Dicom.Tests.Integration.Persistence;
public class QueryStoreTests : IClassFixture<SqlDataStoreTestsFixture>, IAsyncLifetime
{
    private readonly IIndexDataStore _indexDataStore;
    private readonly IIndexDataStoreTestHelper _testHelper;
    private readonly IQueryStore _queryStore;

    public QueryStoreTests(SqlDataStoreTestsFixture fixture)
    {
        EnsureArg.IsNotNull(fixture, nameof(fixture));
        EnsureArg.IsNotNull(fixture.IndexDataStore, nameof(fixture.IndexDataStore));
        EnsureArg.IsNotNull(fixture.IndexDataStoreTestHelper, nameof(fixture.IndexDataStoreTestHelper));
        _indexDataStore = fixture.IndexDataStore;
        _testHelper = fixture.IndexDataStoreTestHelper;
        _queryStore = fixture.QueryStore;
    }

    [Fact]
    public async Task GivenAInstanceWithOnlyRequierdFields_WhenNullableValues_ValidConversion()
    {
        // Dataset will only required field in SQL
        var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        dataset.Add(DicomTag.StudyInstanceUID, TestUidGenerator.Generate());
        dataset.Add(DicomTag.SeriesInstanceUID, TestUidGenerator.Generate());
        dataset.Add(DicomTag.SOPInstanceUID, TestUidGenerator.Generate());
        dataset.Add(DicomTag.PatientID, TestUidGenerator.Generate());
        long version = await _indexDataStore.BeginCreateInstanceIndexAsync(1, dataset);
        await _indexDataStore.EndCreateInstanceIndexAsync(1, dataset, version);

        // test null conversions
        await _queryStore.GetStudyResultAsync(1, new List<long> { version });
        await _queryStore.GetSeriesResultAsync(1, new List<long> { version });
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _testHelper.ClearIndexTablesAsync();
    }
}
