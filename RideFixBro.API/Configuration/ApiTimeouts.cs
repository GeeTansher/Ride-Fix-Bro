namespace RideFixBro.API.Configuration
{
	public static class ApiTimeouts
	{
		// Ek network/SQL operation ka limit hai, poore multi-tool chat ka deadline nahi.
		public const int Seconds = 45;
	}
}
