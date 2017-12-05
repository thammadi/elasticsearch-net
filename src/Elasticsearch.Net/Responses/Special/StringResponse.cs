﻿using System.IO;
using System.Text;

namespace Elasticsearch.Net
{
	public class StringResponse : ElasticsearchResponse<string>
	{
		public StringResponse() { }
		public StringResponse(string body) => this.Body = body;

		public bool TryGetServerError(out ServerError serverError)
		{
			serverError = null;
			if (string.IsNullOrEmpty(this.Body)) return false;
			using(var stream = new MemoryStream(Encoding.UTF8.GetBytes(this.Body)))
				serverError = ServerError.Create(stream);
			return true;
		}
	}
}
