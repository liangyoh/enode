﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ECommon.Components;
using ECommon.Dapper;
using ECommon.IO;
using ECommon.Logging;
using ECommon.Serializing;
using ECommon.Utilities;
using ENode.Eventing;

namespace ENode.SqlServer
{
    public class SqlServerEventStore : IEventStore
    {
        private const string EventSingleTableNameFormat = "[{0}]";
        private const string EventTableNameFormat = "[{0}_{1}]";
        private const string QueryEventsSql = "SELECT * FROM {0} WHERE AggregateRootId = @AggregateRootId AND Version >= @MinVersion AND Version <= @MaxVersion ORDER BY Version ASC";

        #region Private Variables

        private string _connectionString;
        private string _tableName;
        private int _tableCount;
        private string _versionIndexName;
        private string _commandIndexName;
        private int _bulkCopyBatchSize;
        private int _bulkCopyTimeout;
        private IJsonSerializer _jsonSerializer;
        private IEventSerializer _eventSerializer;
        private IOHelper _ioHelper;
        private ILogger _logger;

        #endregion

        #region Public Methods

        public SqlServerEventStore Initialize(
            string connectionString,
            string tableName = "EventStream",
            int tableCount = 1,
            string versionIndexName = "IX_EventStream_AggId_Version",
            string commandIndexName = "IX_EventStream_AggId_CommandId",
            int bulkCopyBatchSize = 1000,
            int bulkCopyTimeoutSeconds = 60)
        {
            _connectionString = connectionString;
            _tableName = tableName;
            _tableCount = tableCount;
            _versionIndexName = versionIndexName;
            _commandIndexName = commandIndexName;
            _bulkCopyBatchSize = bulkCopyBatchSize;
            _bulkCopyTimeout = bulkCopyTimeoutSeconds;

            Ensure.NotNull(_connectionString, "_connectionString");
            Ensure.NotNull(_tableName, "_tableName");
            Ensure.Positive(_tableCount, "_tableCount");
            Ensure.NotNull(_versionIndexName, "_versionIndexName");
            Ensure.NotNull(_commandIndexName, "_commandIndexName");
            Ensure.Positive(_bulkCopyBatchSize, "_bulkCopyBatchSize");
            Ensure.Positive(_bulkCopyTimeout, "_bulkCopyTimeout");

            _jsonSerializer = ObjectContainer.Resolve<IJsonSerializer>();
            _eventSerializer = ObjectContainer.Resolve<IEventSerializer>();
            _ioHelper = ObjectContainer.Resolve<IOHelper>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);

            return this;
        }
        public Task<AsyncTaskResult<IEnumerable<DomainEventStream>>> QueryAggregateEventsAsync(string aggregateRootId, string aggregateRootTypeName, int minVersion, int maxVersion)
        {
            return _ioHelper.TryIOFuncAsync(async () =>
            {
                try
                {
                    using (var connection = GetConnection())
                    {
                        var sql = string.Format(QueryEventsSql, GetTableName(aggregateRootId));
                        var result = await connection.QueryAsync<StreamRecord>(sql, new
                        {
                            AggregateRootId = aggregateRootId,
                            MinVersion = minVersion,
                            MaxVersion = maxVersion
                        }).ConfigureAwait(false);
                        var streams = result.Select(record => ConvertFrom(record));
                        return new AsyncTaskResult<IEnumerable<DomainEventStream>>(AsyncTaskStatus.Success, streams);
                    }
                }
                catch (SqlException ex)
                {
                    var errorMessage = string.Format("Failed to query aggregate events async, aggregateRootId: {0}, aggregateRootType: {1}", aggregateRootId, aggregateRootTypeName);
                    _logger.Error(errorMessage, ex);
                    return new AsyncTaskResult<IEnumerable<DomainEventStream>>(AsyncTaskStatus.IOException, ex.Message);
                }
                catch (Exception ex)
                {
                    var errorMessage = string.Format("Failed to query aggregate events async, aggregateRootId: {0}, aggregateRootType: {1}", aggregateRootId, aggregateRootTypeName);
                    _logger.Error(errorMessage, ex);
                    return new AsyncTaskResult<IEnumerable<DomainEventStream>>(AsyncTaskStatus.Failed, ex.Message);
                }
            }, "QueryAggregateEventsAsync");
        }
        public Task<AsyncTaskResult<EventAppendResult>> BatchAppendAsync(IEnumerable<DomainEventStream> eventStreams)
        {
            if (eventStreams.Count() == 0)
            {
                return Task.FromResult(new AsyncTaskResult<EventAppendResult>(AsyncTaskStatus.Success, new EventAppendResult()));
            }

            var eventStreamDict = new Dictionary<string, IList<DomainEventStream>>();
            var aggregateRootIdList = eventStreams.Select(x => x.AggregateRootId).Distinct().ToList();
            foreach (var aggregateRootId in aggregateRootIdList)
            {
                var eventStreamList = eventStreams.Where(x => x.AggregateRootId == aggregateRootId).ToList();
                if (eventStreamList.Count > 0)
                {
                    eventStreamDict.Add(aggregateRootId, eventStreamList);
                }
            }

            var batchAggregateEventAppendResult = new BatchAggregateEventAppendResult(eventStreamDict.Keys.Count);
            foreach (var entry in eventStreamDict)
            {
                BatchAppendAggregateEventsAsync(entry.Key, entry.Value, batchAggregateEventAppendResult, 0);
            }

            return batchAggregateEventAppendResult.TaskCompletionSource.Task;
        }

        public Task<AsyncTaskResult<DomainEventStream>> FindAsync(string aggregateRootId, int version)
        {
            return _ioHelper.TryIOFuncAsync(async () =>
            {
                try
                {
                    using (var connection = GetConnection())
                    {
                        var result = await connection.QueryListAsync<StreamRecord>(new { AggregateRootId = aggregateRootId, Version = version }, GetTableName(aggregateRootId)).ConfigureAwait(false);
                        var record = result.SingleOrDefault();
                        var stream = record != null ? ConvertFrom(record) : null;
                        return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Success, stream);
                    }
                }
                catch (SqlException ex)
                {
                    _logger.Error(string.Format("Find event by version has sql exception, aggregateRootId: {0}, version: {1}", aggregateRootId, version), ex);
                    return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.IOException, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Find event by version has unknown exception, aggregateRootId: {0}, version: {1}", aggregateRootId, version), ex);
                    return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Failed, ex.Message);
                }
            }, "FindEventByVersionAsync");
        }
        public Task<AsyncTaskResult<DomainEventStream>> FindAsync(string aggregateRootId, string commandId)
        {
            return _ioHelper.TryIOFuncAsync(async () =>
            {
                try
                {
                    using (var connection = GetConnection())
                    {
                        var result = await connection.QueryListAsync<StreamRecord>(new { AggregateRootId = aggregateRootId, CommandId = commandId }, GetTableName(aggregateRootId)).ConfigureAwait(false);
                        var record = result.SingleOrDefault();
                        var stream = record != null ? ConvertFrom(record) : null;
                        return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Success, stream);
                    }
                }
                catch (SqlException ex)
                {
                    _logger.Error(string.Format("Find event by commandId has sql exception, aggregateRootId: {0}, commandId: {1}", aggregateRootId, commandId), ex);
                    return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.IOException, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("Find event by commandId has unknown exception, aggregateRootId: {0}, commandId: {1}", aggregateRootId, commandId), ex);
                    return new AsyncTaskResult<DomainEventStream>(AsyncTaskStatus.Failed, ex.Message);
                }
            }, "FindEventByCommandIdAsync");
        }

        #endregion

        #region Private Methods

        private void BatchAppendAggregateEventsAsync(string aggregateRootId, IList<DomainEventStream> eventStreamList, BatchAggregateEventAppendResult batchAggregateEventAppendResult, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("BatchAppendAggregateEventsAsync",
            () => BatchAppendAggregateEventsAsync(aggregateRootId, eventStreamList),
            currentRetryTimes => BatchAppendAggregateEventsAsync(aggregateRootId, eventStreamList, batchAggregateEventAppendResult, currentRetryTimes),
            result =>
            {
                var eventAppendStatus = result.Data;
                if (eventAppendStatus == EventAppendStatus.Success)
                {
                    batchAggregateEventAppendResult.AddCompleteAggregate(aggregateRootId, new AggregateEventAppendResult
                    {
                        EventAppendStatus = EventAppendStatus.Success
                    });
                }
                else if (eventAppendStatus == EventAppendStatus.DuplicateEvent)
                {
                    batchAggregateEventAppendResult.AddCompleteAggregate(aggregateRootId, new AggregateEventAppendResult
                    {
                        EventAppendStatus = EventAppendStatus.DuplicateEvent
                    });
                }
                else if (eventAppendStatus == EventAppendStatus.DuplicateCommand)
                {
                    foreach (var eventStream in eventStreamList)
                    {
                        TryFindEventByCommandIdAsync(aggregateRootId, eventStream.CommandId, batchAggregateEventAppendResult, 0);
                    }
                }
            },
            () => string.Format("[aggregateRootId: {0}, eventStreamCount: {1}]", aggregateRootId, eventStreamList.Count),
            null,
            retryTimes, true);
        }
        private async Task<AsyncTaskResult<EventAppendStatus>> BatchAppendAggregateEventsAsync(string aggregateRootId, IList<DomainEventStream> eventStreamList)
        {
            try
            {
                var table = BuildEventTable();
                foreach (var eventStream in eventStreamList)
                {
                    AddDataRow(table, eventStream);
                }

                using (var connection = GetConnection())
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    var transaction = await Task.Run(() => connection.BeginTransaction()).ConfigureAwait(false);

                    using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                    {
                        InitializeSqlBulkCopy(copy, aggregateRootId);
                        try
                        {
                            await copy.WriteToServerAsync(table.CreateDataReader()).ConfigureAwait(false);
                        }
                        catch
                        {
                            try
                            {
                                transaction.Rollback();
                            }
                            catch (Exception ex)
                            {
                                _logger.ErrorFormat("BatchAppendAggregateEvents transaction rollback failed, aggregateRootId:" + aggregateRootId, ex);
                            }
                            throw;
                        }
                    }

                    await Task.Run(() => transaction.Commit()).ConfigureAwait(false);
                    return new AsyncTaskResult<EventAppendStatus>(AsyncTaskStatus.Success, EventAppendStatus.Success);
                }
            }
            catch (SqlException ex)
            {
                if (ex.Number == 2601 && ex.Message.Contains(_versionIndexName))
                {
                    return new AsyncTaskResult<EventAppendStatus>(AsyncTaskStatus.Success, EventAppendStatus.DuplicateEvent);
                }
                else if (ex.Number == 2601 && ex.Message.Contains(_commandIndexName))
                {
                    return new AsyncTaskResult<EventAppendStatus>(AsyncTaskStatus.Success, EventAppendStatus.DuplicateCommand);
                }
                throw;
            }
        }
        private void TryFindEventByCommandIdAsync(string aggregateRootId, string commandId, BatchAggregateEventAppendResult batchAggregateEventAppendResult, int retryTimes)
        {
            _ioHelper.TryAsyncActionRecursively("TryFindEventByCommandIdAsync",
            () => FindAsync(aggregateRootId, commandId),
            currentRetryTimes => TryFindEventByCommandIdAsync(aggregateRootId, commandId, batchAggregateEventAppendResult, currentRetryTimes),
            result =>
            {
                if (result.Data != null)
                {
                    batchAggregateEventAppendResult.AddCompleteAggregate(aggregateRootId, new AggregateEventAppendResult
                    {
                        EventAppendStatus = EventAppendStatus.DuplicateCommand,
                        DuplicateCommandId = result.Data.CommandId
                    });
                }
            },
            () => string.Format("[aggregateRootId:{0}, commandId:{1}]", aggregateRootId, commandId),
            null,
            retryTimes, true);
        }
        private int GetTableIndex(string aggregateRootId)
        {
            int hash = 23;
            foreach (char c in aggregateRootId)
            {
                hash = (hash << 5) - hash + c;
            }
            if (hash < 0)
            {
                hash = Math.Abs(hash);
            }
            return hash % _tableCount;
        }
        private string GetTableName(string aggregateRootId)
        {
            if (_tableCount <= 1)
            {
                return string.Format(EventSingleTableNameFormat, _tableName);
            }

            var tableIndex = GetTableIndex(aggregateRootId);

            return string.Format(EventTableNameFormat, _tableName, tableIndex);
        }
        private SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
        private DomainEventStream ConvertFrom(StreamRecord record)
        {
            return new DomainEventStream(
                record.CommandId,
                record.AggregateRootId,
                record.AggregateRootTypeName,
                record.CreatedOn,
                _eventSerializer.Deserialize<IDomainEvent>(_jsonSerializer.Deserialize<IDictionary<string, string>>(record.Events)));
        }
        private StreamRecord ConvertTo(DomainEventStream eventStream)
        {
            return new StreamRecord
            {
                CommandId = eventStream.CommandId,
                AggregateRootId = eventStream.AggregateRootId,
                AggregateRootTypeName = eventStream.AggregateRootTypeName,
                Version = eventStream.Version,
                CreatedOn = eventStream.Timestamp,
                Events = _jsonSerializer.Serialize(_eventSerializer.Serialize(eventStream.Events))
            };
        }
        private DataTable BuildEventTable()
        {
            var table = new DataTable();
            table.Columns.Add("AggregateRootId", typeof(string));
            table.Columns.Add("AggregateRootTypeName", typeof(string));
            table.Columns.Add("Version", typeof(int));
            table.Columns.Add("CommandId", typeof(string));
            table.Columns.Add("CreatedOn", typeof(DateTime));
            table.Columns.Add("Events", typeof(string));
            return table;
        }
        private void AddDataRow(DataTable table, DomainEventStream eventStream)
        {
            var row = table.NewRow();
            row["AggregateRootId"] = eventStream.AggregateRootId;
            row["AggregateRootTypeName"] = eventStream.AggregateRootTypeName;
            row["CommandId"] = eventStream.CommandId;
            row["Version"] = eventStream.Version;
            row["CreatedOn"] = eventStream.Timestamp;
            row["Events"] = _jsonSerializer.Serialize(_eventSerializer.Serialize(eventStream.Events));
            table.Rows.Add(row);
        }
        private void InitializeSqlBulkCopy(SqlBulkCopy copy, string aggregateRootId)
        {
            copy.BatchSize = _bulkCopyBatchSize;
            copy.BulkCopyTimeout = _bulkCopyTimeout;
            copy.DestinationTableName = GetTableName(aggregateRootId);
            copy.ColumnMappings.Add("AggregateRootId", "AggregateRootId");
            copy.ColumnMappings.Add("AggregateRootTypeName", "AggregateRootTypeName");
            copy.ColumnMappings.Add("CommandId", "CommandId");
            copy.ColumnMappings.Add("Version", "Version");
            copy.ColumnMappings.Add("CreatedOn", "CreatedOn");
            copy.ColumnMappings.Add("Events", "Events");
        }

        #endregion

        class BatchAggregateEventAppendResult
        {
            private ConcurrentDictionary<string, AggregateEventAppendResult> _aggregateEventAppendResultDict = new ConcurrentDictionary<string, AggregateEventAppendResult>();
            public TaskCompletionSource<AsyncTaskResult<EventAppendResult>> TaskCompletionSource = new TaskCompletionSource<AsyncTaskResult<EventAppendResult>>();
            private readonly int _expectedAggregateRootCount;

            public BatchAggregateEventAppendResult(int expectedAggregateRootCount)
            {
                _expectedAggregateRootCount = expectedAggregateRootCount;
            }

            public void AddCompleteAggregate(string aggregateRootId, AggregateEventAppendResult result)
            {
                if (_aggregateEventAppendResultDict.TryAdd(aggregateRootId, result))
                {
                    var completedAggregateRootCount = _aggregateEventAppendResultDict.Keys.Count;
                    if (completedAggregateRootCount == _expectedAggregateRootCount)
                    {
                        var eventAppendResult = new EventAppendResult();
                        foreach (var entry in _aggregateEventAppendResultDict)
                        {
                            if (entry.Value.EventAppendStatus == EventAppendStatus.Success)
                            {
                                eventAppendResult.AddSuccessAggregateRootId(entry.Key);
                            }
                            else if (entry.Value.EventAppendStatus == EventAppendStatus.DuplicateEvent)
                            {
                                eventAppendResult.AddDuplicateEventAggregateRootId(entry.Key);
                            }
                            else if (entry.Value.EventAppendStatus == EventAppendStatus.DuplicateCommand)
                            {
                                eventAppendResult.AddDuplicateCommandId(entry.Value.DuplicateCommandId);
                            }
                        }
                        TaskCompletionSource.TrySetResult(new AsyncTaskResult<EventAppendResult>(AsyncTaskStatus.Success, eventAppendResult));
                    }
                }
            }
        }
        class AggregateEventAppendResult
        {
            public EventAppendStatus EventAppendStatus;
            public string DuplicateCommandId;
        }
        class StreamRecord
        {
            public string AggregateRootTypeName { get; set; }
            public string AggregateRootId { get; set; }
            public int Version { get; set; }
            public string CommandId { get; set; }
            public DateTime CreatedOn { get; set; }
            public string Events { get; set; }
        }
    }
}
