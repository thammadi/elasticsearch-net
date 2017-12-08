﻿using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Elasticsearch.Net
{
	public class Transport<TConnectionSettings> : ITransport<TConnectionSettings>
		where TConnectionSettings : IConnectionConfigurationValues
	{
		public TConnectionSettings Settings { get; }

		private IDateTimeProvider DateTimeProvider { get; }
		private IMemoryStreamFactory MemoryStreamFactory { get; }
		private IRequestPipelineFactory PipelineProvider { get; }

		/// <summary>
		/// Transport coordinates the client requests over the connection pool nodes and is in charge of falling over on different nodes
		/// </summary>
		/// <param name="configurationValues">The connectionsettings to use for this transport</param>
		public Transport(TConnectionSettings configurationValues) : this(configurationValues, null, null, null) { }

		/// <summary>
		/// Transport coordinates the client requests over the connection pool nodes and is in charge of falling over on different nodes
		/// </summary>
		/// <param name="configurationValues">The connectionsettings to use for this transport</param>
		/// <param name="pipelineProvider">In charge of create a new pipeline, safe to pass null to use the default</param>
		/// <param name="dateTimeProvider">The date time proved to use, safe to pass null to use the default</param>
		/// <param name="memoryStreamFactory">The memory stream provider to use, safe to pass null to use the default</param>
		public Transport(
			TConnectionSettings configurationValues,
			IRequestPipelineFactory pipelineProvider,
			IDateTimeProvider dateTimeProvider,
			IMemoryStreamFactory memoryStreamFactory
			)
		{
			configurationValues.ThrowIfNull(nameof(configurationValues));
			configurationValues.ConnectionPool.ThrowIfNull(nameof(configurationValues.ConnectionPool));
			configurationValues.Connection.ThrowIfNull(nameof(configurationValues.Connection));
			configurationValues.RequestResponseSerializer.ThrowIfNull(nameof(configurationValues.RequestResponseSerializer));

			this.Settings = configurationValues;
			this.PipelineProvider = pipelineProvider ?? new RequestPipelineFactory();
			this.DateTimeProvider = dateTimeProvider ?? Elasticsearch.Net.DateTimeProvider.Default;
			this.MemoryStreamFactory = memoryStreamFactory ?? new MemoryStreamFactory();
		}

		public TResponse Request<TResponse>(HttpMethod method, string path, PostData data = null, IRequestParameters requestParameters = null)
			where TResponse : class, IElasticsearchResponse
		{
			using (var pipeline = this.PipelineProvider.Create(this.Settings, this.DateTimeProvider, this.MemoryStreamFactory, requestParameters))
			{
				pipeline.FirstPoolUsage(this.Settings.BootstrapLock);

				var requestData = new RequestData(method, path, data, this.Settings, requestParameters, this.MemoryStreamFactory);
				this.Settings.OnRequestDataCreated?.Invoke(requestData);
				TResponse response = null;

				var seenExceptions = new List<PipelineException>();
				foreach (var node in pipeline.NextNode())
				{
					requestData.Node = node;
					try
					{
						pipeline.SniffOnStaleCluster();
						Ping(pipeline, node);
						response = pipeline.CallElasticsearch<TResponse>(requestData);
						if (!response.ApiCall.SuccessOrKnownError)
						{
							pipeline.MarkDead(node);
							pipeline.SniffOnConnectionFailure();
						}
					}
					catch (PipelineException pipelineException) when (!pipelineException.Recoverable)
					{
						pipeline.MarkDead(node);
						seenExceptions.Add(pipelineException);
						break;
					}
					catch (PipelineException pipelineException)
					{
						pipeline.MarkDead(node);
						seenExceptions.Add(pipelineException);
					}
					catch (Exception killerException)
					{
						throw new UnexpectedElasticsearchClientException(killerException, seenExceptions)
						{
							Request = requestData,
							Response = response.ApiCall,
							AuditTrail = pipeline?.AuditTrail
						};
					}
					if (response == null || !response.ApiCall.SuccessOrKnownError) continue;
					pipeline.MarkAlive(node);
					break;
				}
				if (requestData.Node == null) //foreach never ran
					pipeline.ThrowNoNodesAttempted(requestData, seenExceptions);

				if (response == null || !response.ApiCall.Success)
					pipeline.BadResponse(ref response, requestData, seenExceptions);

				this.Settings.OnRequestCompleted?.Invoke(response.ApiCall);

				return response;
			}
		}

		public async Task<TResponse> RequestAsync<TResponse>(HttpMethod method, string path, CancellationToken cancellationToken, PostData data = null, IRequestParameters requestParameters = null)
			where TResponse : class, IElasticsearchResponse
		{
			using (var pipeline = this.PipelineProvider.Create(this.Settings, this.DateTimeProvider, this.MemoryStreamFactory, requestParameters))
			{
				await pipeline.FirstPoolUsageAsync(this.Settings.BootstrapLock, cancellationToken).ConfigureAwait(false);

				var requestData = new RequestData(method, path, data, this.Settings, requestParameters, this.MemoryStreamFactory);
				this.Settings.OnRequestDataCreated?.Invoke(requestData);
				TResponse response = null;

				var seenExceptions = new List<PipelineException>();
				foreach (var node in pipeline.NextNode())
				{
					requestData.Node = node;
					try
					{
						await pipeline.SniffOnStaleClusterAsync(cancellationToken).ConfigureAwait(false);
						await PingAsync(pipeline, node, cancellationToken).ConfigureAwait(false);
						response = await pipeline.CallElasticsearchAsync<TResponse>(requestData, cancellationToken).ConfigureAwait(false);
						if (!response.ApiCall.SuccessOrKnownError)
						{
							pipeline.MarkDead(node);
							await pipeline.SniffOnConnectionFailureAsync(cancellationToken).ConfigureAwait(false);
						}
					}
					catch (PipelineException pipelineException) when (!pipelineException.Recoverable)
					{
						pipeline.MarkDead(node);
						seenExceptions.Add(pipelineException);
						break;
					}
					catch (PipelineException pipelineException)
					{
						pipeline.MarkDead(node);
						seenExceptions.Add(pipelineException);
					}
					catch (Exception killerException)
					{
						throw new UnexpectedElasticsearchClientException(killerException, seenExceptions)
						{
							Request = requestData,
							Response = response.ApiCall,
							AuditTrail = pipeline.AuditTrail
						};
					}
					if (cancellationToken.IsCancellationRequested)
					{
						pipeline.AuditCancellationRequested();
						break;
					}
					if (response == null || !response.ApiCall.SuccessOrKnownError) continue;
					pipeline.MarkAlive(node);
					break;
				}
				if (requestData.Node == null) //foreach never ran
					pipeline.ThrowNoNodesAttempted(requestData, seenExceptions);

				if (response == null || !response.ApiCall.Success)
					pipeline.BadResponse(ref response, requestData, seenExceptions);

				this.Settings.OnRequestCompleted?.Invoke(response.ApiCall);

				return response;
			}
		}

		private static void Ping(IRequestPipeline pipeline, Node node)
		{
			try
			{
				pipeline.Ping(node);
			}
			catch (PipelineException e) when (e.Recoverable)
			{
				pipeline.SniffOnConnectionFailure();
				throw;
			}
		}

		private static async Task PingAsync(IRequestPipeline pipeline, Node node, CancellationToken cancellationToken)
		{
			try
			{
				await pipeline.PingAsync(node, cancellationToken).ConfigureAwait(false);
			}
			catch (PipelineException e) when (e.Recoverable)
			{
				await pipeline.SniffOnConnectionFailureAsync(cancellationToken).ConfigureAwait(false);
				throw;
			}
		}

	}
}
