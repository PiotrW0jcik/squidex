﻿// ==========================================================================
//  MongoEventStore.cs
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex Group
//  All rights reserved.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Squidex.Infrastructure.CQRS.Events;
using Squidex.Infrastructure.Reflection;

// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable UnusedMember.Local
// ReSharper disable InvertIf

namespace Squidex.Infrastructure.MongoDb.EventStore
{
    public class MongoEventStore : MongoRepositoryBase<MongoEventCommit>, IEventStore
    {
        private const int Retries = 500;
        private readonly IEventNotifier notifier;
        private string eventsOffsetIndex;

        public MongoEventStore(IMongoDatabase database, IEventNotifier notifier) 
            : base(database)
        {
            Guard.NotNull(notifier, nameof(notifier));

            this.notifier = notifier;
        }

        protected override string CollectionName()
        {
            return "Events";
        }

        protected override MongoCollectionSettings CollectionSettings()
        {
            return new MongoCollectionSettings { WriteConcern = WriteConcern.WMajority };
        }

        protected override async Task SetupCollectionAsync(IMongoCollection<MongoEventCommit> collection)
        {
            var indexNames =
                await Task.WhenAll(
                    collection.Indexes.CreateOneAsync(IndexKeys.Descending(x => x.EventsOffset), new CreateIndexOptions { Unique = true }),
                    collection.Indexes.CreateOneAsync(IndexKeys.Descending(x => x.EventStreamOffset).Ascending(x => x.EventStream), new CreateIndexOptions { Unique = true }));

            eventsOffsetIndex = indexNames[0];
        }

        public IObservable<StoredEvent> GetEventsAsync(string streamName)
        {
            Guard.NotNullOrEmpty(streamName, nameof(streamName));

            return Observable.Create<StoredEvent>(async (observer, ct) =>
            {
                await Collection.Find(x => x.EventStream == streamName).ForEachAsync(commit =>
                {
                    var position = commit.EventStreamOffset;

                    foreach (var @event in commit.Events)
                    {
                        var eventData = SimpleMapper.Map(@event, new EventData());

                        observer.OnNext(new StoredEvent(position, eventData));

                        position++;
                    }
                }, ct);
            });
        }

        public IObservable<StoredEvent> GetEventsAsync(long lastReceivedPosition = -1)
        {
            return Observable.Create<StoredEvent>(async (observer, ct) =>
            {
                var commitOffset = await GetPreviousOffset(lastReceivedPosition);

                await Collection.Find(x => x.EventsOffset >= commitOffset).SortBy(x => x.EventsOffset).ForEachAsync(commit =>
                {
                    var eventNumber = commit.EventsOffset;

                    foreach (var @event in commit.Events)
                    {
                        eventNumber++;

                        if (eventNumber > lastReceivedPosition)
                        {
                            var eventData = SimpleMapper.Map(@event, new EventData());

                            observer.OnNext(new StoredEvent(eventNumber, eventData));
                        }
                    }
                }, ct);
            });
        }

        public async Task AppendEventsAsync(Guid commitId, string streamName, int expectedVersion, IEnumerable<EventData> events)
        {
            Guard.NotNullOrEmpty(streamName, nameof(streamName));
            Guard.NotNull(events, nameof(events));

            var currentVersion = await GetEventStreamOffset(streamName);

            if (currentVersion != expectedVersion)
            {
                throw new WrongEventVersionException(currentVersion, expectedVersion);
            }

            var now = DateTime.UtcNow;

            var commitEvents = events.Select(x => SimpleMapper.Map(x, new MongoEvent())).ToList();

            if (commitEvents.Any())
            {
                var offset = await GetEventOffset();

                var commit = new MongoEventCommit
                {
                    Id = commitId,
                    Events = commitEvents,
                    EventsOffset = offset,
                    EventsCount = commitEvents.Count,
                    EventStream = streamName,
                    EventStreamOffset = expectedVersion,
                    Timestamp = now
                };

                for (var retry = 0; retry < Retries; retry++)
                {
                    try
                    {
                        await Collection.InsertOneAsync(commit);

                        notifier.NotifyEventsStored();

                        return;
                    }
                    catch (MongoWriteException e)
                    {
                        if (e.Message.IndexOf(eventsOffsetIndex, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            commit.EventsOffset = await GetEventOffset();
                        }
                        else if (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                        {
                            currentVersion = await GetEventStreamOffset(streamName);

                            throw new WrongEventVersionException(currentVersion, expectedVersion);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
        }

        private async Task<long> GetPreviousOffset(long startPosition)
        {
            var document =
                await Collection.Find(x => x.EventsOffset <= startPosition)
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventStreamOffset)
                        .Include(x => x.EventsCount))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventStreamOffset"].ToInt64();
            }

            return -1;
        }

        private async Task<long> GetEventOffset()
        {
            var document =
                await Collection.Find(new BsonDocument())
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventsOffset)
                        .Include(x => x.EventsCount))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventsOffset"].ToInt64() + document["EventsCount"].ToInt64();
            }

            return -1;
        }

        private async Task<long> GetEventStreamOffset(string streamName)
        {
            var document =
                await Collection.Find(x => x.EventStream == streamName)
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventStreamOffset)
                        .Include(x => x.EventsCount))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventStreamOffset"].ToInt64() + document["EventsCount"].ToInt64();
            }

            return -1;
        }
    }
}
