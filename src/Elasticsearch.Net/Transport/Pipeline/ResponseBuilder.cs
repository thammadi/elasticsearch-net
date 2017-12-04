﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Elasticsearch.Net
{
	public static class ResponseBuilder
	{
		private const int BufferSize = 81920;

		internal static readonly IDisposable EmptyDisposable = new MemoryStream();

		public static TResponse ToResponse<TResponse>(RequestData requestData, Exception ex, int? statusCode, IEnumerable<string> warnings, Stream responseStream)
			where TResponse : class, IElasticsearchResponse, new()
		{
			responseStream.ThrowIfNull(nameof(responseStream));
			var details = Initialize(requestData, ex, statusCode, warnings);
			var response = SetBody<TResponse>(details, requestData, responseStream) ?? new TResponse();
			response.ApiCall = details;
			return response;
		}

		public static async Task<TResponse> ToResponseAsync<TResponse>(
			RequestData requestData,
			Exception ex,
			int? statusCode,
			IEnumerable<string> warnings,
			Stream responseStream,
			CancellationToken cancellationToken)
			where TResponse : class, IElasticsearchResponse, new()
		{
			responseStream.ThrowIfNull(nameof(responseStream));
			var details = Initialize(requestData, ex, statusCode, warnings);
			var response = (await SetBodyAsync<TResponse>(details, requestData, responseStream, cancellationToken).ConfigureAwait(false))
				?? new TResponse();
			response.ApiCall = details;
			return response;
		}

		private static HttpDetails Initialize(RequestData requestData, Exception exception, int? statusCode, IEnumerable<string> warnings)
		{
			var success = false;
			var allowedStatusCodes = requestData.AllowedStatusCodes.ToList();
			if (statusCode.HasValue)
			{
				success = statusCode >= 200 && statusCode < 300
				          || (requestData.Method == HttpMethod.HEAD && statusCode == 404)
				          || allowedStatusCodes.Contains(statusCode.Value)
				          || allowedStatusCodes.Contains(-1);
			}
			var httpCallDetails = new HttpDetails
			{
				Success = success,
				OriginalException = exception,
				HttpStatusCode = statusCode,
				RequestBodyInBytes = requestData.PostData?.WrittenBytes,
				Uri = requestData.Uri,
				HttpMethod = requestData.Method,
				DeprecationWarnings = warnings ?? Enumerable.Empty<string>()
			};
			return httpCallDetails;
		}

		private static TResponse SetBody<TResponse>(HttpDetails details, RequestData requestData, Stream responseStream)
			where TResponse : class, IElasticsearchResponse, new()
		{
			byte[] bytes = null;
			var disableDirectStreaming = requestData.PostData?.DisableDirectStreaming ?? requestData.ConnectionSettings.DisableDirectStreaming;
			if (disableDirectStreaming || NeedsToEagerReadStream<TResponse>())
			{
				var inMemoryStream = requestData.MemoryStreamFactory.Create();
				responseStream.CopyTo(inMemoryStream, BufferSize);
				bytes = SwapStreams(ref responseStream, ref inMemoryStream);
				details.ResponseBodyInBytes = bytes;
			}

			var needsDispose = typeof(TResponse) != typeof(ElasticsearchResponse<Stream>);
			using (needsDispose ? responseStream : EmptyDisposable)
			{
				if (SetSpecialTypes<TResponse>(responseStream, bytes, out var r))
					return r;

				if (details.HttpStatusCode.HasValue && requestData.SkipDeserializationForStatusCodes.Contains(details.HttpStatusCode.Value))
					return null;

				if (requestData.CustomConverter != null) return requestData.CustomConverter(details, responseStream) as TResponse;
				return requestData.ConnectionSettings.RequestResponseSerializer.Deserialize<TResponse>(responseStream);
			}
		}

		private static async Task<TResponse> SetBodyAsync<TResponse>(HttpDetails details, RequestData requestData, Stream responseStream, CancellationToken cancellationToken)
			where TResponse : class, IElasticsearchResponse, new()
		{
			byte[] bytes = null;
			var disableDirectStreaming = requestData.PostData?.DisableDirectStreaming ?? requestData.ConnectionSettings.DisableDirectStreaming;
			if (disableDirectStreaming || NeedsToEagerReadStream<TResponse>())
			{
				var inMemoryStream = requestData.MemoryStreamFactory.Create();
				await responseStream.CopyToAsync(inMemoryStream, BufferSize, cancellationToken).ConfigureAwait(false);
				bytes = SwapStreams(ref responseStream, ref inMemoryStream);
				details.ResponseBodyInBytes = bytes;
			}

			var needsDispose = typeof(TResponse) != typeof(ElasticsearchResponse<Stream>);
			using (needsDispose ? responseStream : EmptyDisposable)
			{
				if (SetSpecialTypes<TResponse>(responseStream, bytes, out var r)) return r;

				if (details.HttpStatusCode.HasValue && requestData.SkipDeserializationForStatusCodes.Contains(details.HttpStatusCode.Value))
					return null;

				if (requestData.CustomConverter != null) return requestData.CustomConverter(details, responseStream) as TResponse;
				return await requestData.ConnectionSettings.RequestResponseSerializer.DeserializeAsync<TResponse>(responseStream, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		private static readonly VoidResponse StaticVoid = new VoidResponse { Body = new VoidResponse.VoidBody() };
		private static readonly Type[] SpecialTypes = {typeof(StringResponse), typeof(BytesResponse), typeof(VoidResponse), typeof(StreamResponse)};

		private static bool SetSpecialTypes<TResponse>(Stream responseStream, byte[] bytes, out TResponse cs)
			where TResponse : class, IElasticsearchResponse, new()
		{
			cs = null;
			var responseType = typeof(TResponse);
			if (!SpecialTypes.Contains(responseType)) return false;

			if (responseType == typeof(StringResponse))
				cs = new StringResponse(bytes.Utf8String()) as TResponse;
			else if (responseType == typeof(byte[]))
				cs = new BytesResponse(bytes) as TResponse;
			else if (responseType == typeof(VoidResponse))
				cs = StaticVoid as TResponse;
			else if (responseType == typeof(StreamResponse))
				cs = new StreamResponse(responseStream) as TResponse;

			return cs != null;
		}

		private static bool NeedsToEagerReadStream<TResponse>()
			where TResponse : class, IElasticsearchResponse, new() =>
			typeof(TResponse) == typeof(StringResponse) || typeof(TResponse) == typeof(BytesResponse);

		private static byte[] SwapStreams(ref Stream responseStream, ref MemoryStream ms)
		{
			var bytes = ms.ToArray();
			responseStream.Dispose();
			responseStream = ms;
			responseStream.Position = 0;
			return bytes;
		}
	}
}
