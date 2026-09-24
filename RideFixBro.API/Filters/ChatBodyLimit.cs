using Microsoft.AspNetCore.Mvc;

namespace RideFixBro.API.Filters
{
	public sealed class ChatBodyLimit(long bytes) : RequestSizeLimitAttribute(bytes)
	{
    }
}
