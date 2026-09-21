using DVSRegister.CommonUtility.Models;
using DVSRegister.CommonUtility.Models.Enums;
using DVSRegister.Data;
using DVSRegister.Data.Entities;
using DVSRegister.UnitTests.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DVSRegister.UnitTests.Migrations
{
    [Collection("Postgres Collection")]
    public class BackfillServiceRemovedTimeFromRemovalRequestsTests : IAsyncLifetime
    {
        private const string PreviousMigration = "20260818132102_AddServiceTypeToReport";
        private const string BackfillMigration = "20260918092539_BackfillServiceRemovedTimeFromRemovalRequests";
        private readonly PostgresTestFixture fixture;

        public BackfillServiceRemovedTimeFromRemovalRequestsTests(PostgresTestFixture fixture)
        {
            this.fixture = fixture;
        }

        public Task InitializeAsync() => fixture.ResetToMigrationAsync(PreviousMigration);

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task Up_BackfillsLatestCompletedRemovalTimeWithoutOverwritingExistingValues()
        {
            var earlierRemovalTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
            var latestRemovalTime = new DateTime(2026, 1, 12, 15, 30, 0, DateTimeKind.Utc);
            var existingRemovalTime = new DateTime(2026, 1, 8, 12, 0, 0, DateTimeKind.Utc);
            var newerRequestRemovalTime = new DateTime(2026, 1, 14, 10, 0, 0, DateTimeKind.Utc);
            int serviceToBackfillId;
            int serviceWithExistingValueId;
            int serviceWithoutCompletedRequestId;

            await using (var setupContext = CreateDbContext())
            {
                var provider = RepositoryTestHelper.CreateProviderProfile(1, "Migration test provider");
                setupContext.ProviderProfile.Add(provider);
                await setupContext.SaveChangesAsync();

                var serviceToBackfill = CreateService(provider.Id, 1001, "Service to backfill");
                var serviceWithExistingValue = CreateService(provider.Id, 1002, "Service with existing removal time");
                serviceWithExistingValue.RemovedTime = existingRemovalTime;
                var serviceWithoutCompletedRequest = CreateService(provider.Id, 1003, "Service without completed request");

                setupContext.Service.AddRange(serviceToBackfill, serviceWithExistingValue, serviceWithoutCompletedRequest);
                await setupContext.SaveChangesAsync();

                serviceToBackfillId = serviceToBackfill.Id;
                serviceWithExistingValueId = serviceWithExistingValue.Id;
                serviceWithoutCompletedRequestId = serviceWithoutCompletedRequest.Id;

                setupContext.ServiceRemovalRequest.AddRange(
                    CreateRemovalRequest(serviceToBackfillId, earlierRemovalTime),
                    CreateRemovalRequest(serviceToBackfillId, latestRemovalTime),
                    CreateRemovalRequest(serviceWithExistingValueId, newerRequestRemovalTime),
                    CreateRemovalRequest(serviceWithoutCompletedRequestId, null, true),
                    CreateRemovalRequest(serviceWithoutCompletedRequestId, null, false));
                await setupContext.SaveChangesAsync();
            }

            await using (var migrationContext = CreateDbContext())
            {
                var migrator = migrationContext.Database.GetService<IMigrator>();
                await migrator.MigrateAsync(BackfillMigration);
            }

            await using var verificationContext = CreateDbContext();
            var serviceToBackfillResult = await verificationContext.Service.SingleAsync(s => s.Id == serviceToBackfillId);
            var serviceWithExistingValueResult = await verificationContext.Service.SingleAsync(s => s.Id == serviceWithExistingValueId);
            var serviceWithoutCompletedRequestResult = await verificationContext.Service.SingleAsync(s => s.Id == serviceWithoutCompletedRequestId);

            Assert.Equal(latestRemovalTime, serviceToBackfillResult.RemovedTime);
            Assert.Equal(existingRemovalTime, serviceWithExistingValueResult.RemovedTime);
            Assert.Null(serviceWithoutCompletedRequestResult.RemovedTime);
        }

        private DVSRegisterDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<DVSRegisterDbContext>()
                .UseNpgsql(fixture.GetConnectionString())
                .Options;
            return new DVSRegisterDbContext(options);
        }

        private static Service CreateService(int providerProfileId, int serviceKey, string serviceName)
        {
            return RepositoryTestHelper.CreateService(
                1,
                serviceName,
                providerProfileId,
                ServiceStatusEnum.Removed,
                false,
                false,
                false,
                serviceKey);
        }

        private static ServiceRemovalRequest CreateRemovalRequest(int serviceId, DateTime? removedTime, bool isPending = false)
        {
            return new ServiceRemovalRequest
            {
                ServiceId = serviceId,
                IsRequestPending = isPending,
                RemovedTime = removedTime,
                PreviousServiceStatus = ServiceStatusEnum.Published
            };
        }
    }
}