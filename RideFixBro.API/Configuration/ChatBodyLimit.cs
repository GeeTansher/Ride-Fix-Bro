using Microsoft.AspNetCore.Mvc;

namespace RideFixBro.API.Configuration
{
	public sealed class ChatBodyLimit(long bytes) : RequestSizeLimitAttribute(bytes)
	{
    }
}
