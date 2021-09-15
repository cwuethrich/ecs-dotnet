// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Elastic.Ingest;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Ingest.Elasticsearch.Indices;
using Elastic.Ingest.Transport;
using Elastic.Transport;
using Elasticsearch.Extensions.Logging.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elasticsearch.Extensions.Logging
{
	[ProviderAlias("Elasticsearch")]
	public class ElasticsearchLoggerProvider : ILoggerProvider, ISupportExternalScope
	{
		private readonly IChannelSetup[] _channelConfigurations;
		private readonly IOptionsMonitor<ElasticsearchLoggerOptions> _options;
		private readonly IDisposable _optionsReloadToken;
		private IExternalScopeProvider? _scopeProvider;
		private IIngestChannel<LogEvent> _shipper;

		public ElasticsearchLoggerProvider(IOptionsMonitor<ElasticsearchLoggerOptions> options,
			IEnumerable<IChannelSetup> channelConfigurations
		)
		{
			_options = options ?? throw new ArgumentNullException(nameof(options));

			if (channelConfigurations is null)
				throw new ArgumentNullException(nameof(channelConfigurations));

			_channelConfigurations = channelConfigurations.ToArray();

			_shipper = CreatIngestChannel(options.CurrentValue);
			_optionsReloadToken = _options.OnChange(o => ReloadShipper(o));
		}

		public static Func<DateTimeOffset> LocalDateTimeProvider { get; set; } = () => DateTimeOffset.UtcNow;

		public ILogger CreateLogger(string name) =>
			new ElasticsearchLogger(name, _shipper, _options.CurrentValue, _scopeProvider);

		public void Dispose()
		{
			_optionsReloadToken.Dispose();
			_shipper.Dispose();
		}

		public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

		private static void SetupChannelOptions(
			IChannelSetup[] channelConfigurations,
			ElasticsearchChannelOptionsBase<LogEvent> channelOptions
		)
		{
			foreach (var channelSetup in channelConfigurations)
				channelSetup.ConfigureChannel(channelOptions);
		}
		public static IConnectionPool CreateConnectionPool(ElasticsearchLoggerOptions loggerOptions)
		{
			var shipTo = loggerOptions.ShipTo;
			var connectionPool = loggerOptions.ShipTo.ConnectionPoolType;
			var nodeUris = loggerOptions.ShipTo.NodeUris?.ToArray() ?? Array.Empty<Uri>();
			if (nodeUris.Length == 0 && connectionPool != ConnectionPoolType.Cloud)
				return new SingleNodeConnectionPool(new Uri("http://localhost:9200"));
			if (connectionPool == ConnectionPoolType.SingleNode || connectionPool == ConnectionPoolType.Unknown && nodeUris.Length == 1)
				return new SingleNodeConnectionPool(nodeUris[0]);

			switch (connectionPool)
			{
				// TODO: Add option to randomize pool
				case ConnectionPoolType.Unknown:
				case ConnectionPoolType.Sniffing:
					return new SniffingConnectionPool(nodeUris);
				case ConnectionPoolType.Static:
					return new StaticConnectionPool(nodeUris);
				case ConnectionPoolType.Sticky:
					return new StickyConnectionPool(nodeUris);
				// case ConnectionPoolType.StickySniffing:
				case ConnectionPoolType.Cloud:
					if (!string.IsNullOrEmpty(shipTo.ApiKey))
					{
						var apiKeyCredentials = new ApiKey(shipTo.ApiKey);
						return new CloudConnectionPool(shipTo.CloudId, apiKeyCredentials);
					}

					var basicAuthCredentials = new BasicAuthentication(shipTo.Username, shipTo.Password);
					return new CloudConnectionPool(shipTo.CloudId, basicAuthCredentials);
				default:
					throw new ArgumentException($"Unrecognised connection pool type '{connectionPool}' specified in the configuration.", nameof(connectionPool));
			}
		}

		private static ITransport<ITransportConfiguration> CreateTransport(ElasticsearchLoggerOptions loggerOptions)
		{
			// TODO: Check if Uri has changed before recreating
			// TODO: Injectable factory? Or some way of testing.
			var connectionPool = CreateConnectionPool(loggerOptions);
			var config = new TransportConfiguration(connectionPool);

			// config = config.Proxy(new Uri("http://localhost:8080"), "", "");
			// config = config.EnableDebugMode();

			var transport = new Transport<TransportConfiguration>(config);
			return transport;
		}

		private void ReloadShipper(ElasticsearchLoggerOptions loggerOptions)
		{
			var newShipper = CreatIngestChannel(loggerOptions);
			var oldShipper = Interlocked.Exchange(ref _shipper, newShipper);
			oldShipper?.Dispose();
		}

		private IIngestChannel<LogEvent> CreatIngestChannel(ElasticsearchLoggerOptions loggerOptions)
		{
			var transport = CreateTransport(loggerOptions);
			if (loggerOptions.Index != null)
			{
				var indexChannelOptions = new IndexChannelOptions<LogEvent>(transport)
				{
					Index = loggerOptions.Index.Format,
					IndexOffset = loggerOptions.Index.IndexOffset,
					WriteEvent = async (stream, ctx, logEvent) => await logEvent.SerializeAsync(stream, ctx).ConfigureAwait(false),
					TimestampLookup = l => l.Timestamp
				};
				SetupChannelOptions(_channelConfigurations, indexChannelOptions);
				return new IndexChannel<LogEvent>(indexChannelOptions);
			}
			else
			{
				var dataStreamNameOptions = loggerOptions.DataStream ?? new DataStreamNameOptions();
				var indexChannelOptions = new DataStreamChannelOptions<LogEvent>(transport)
				{
					DataStream = new DataStreamName(dataStreamNameOptions.Type, dataStreamNameOptions.DataSet, dataStreamNameOptions.Namespace),
					WriteEvent = async (stream, ctx, logEvent) => await logEvent.SerializeAsync(stream, ctx).ConfigureAwait(false),
				};
				SetupChannelOptions(_channelConfigurations, indexChannelOptions);
				return new DataStreamChannel<LogEvent>(indexChannelOptions);
			}
		}
	}
}