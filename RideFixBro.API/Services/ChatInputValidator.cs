using RideFixBro.API.Configuration;
using StbImageSharp;

namespace RideFixBro.API.Services
{
	public sealed class ChatInputValidator
	{
		private readonly ChatLimitsOptions _limits;

		public ChatInputValidator(ChatLimitsOptions limits)
		{
			_limits = limits;
		}

		public string? Validate(string sessionId, string message, string? imageData)
		{
			if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > _limits.MaxSessionIdCharacters)
			{
				throw new ChatInputException($"Bhai, SessionId 1 se {_limits.MaxSessionIdCharacters} characters ka hona chahiye.");
			}
			if (string.IsNullOrWhiteSpace(message) || message.Length > _limits.MaxMessageCharacters)
			{
				throw new ChatInputException($"Bhai, message 1 se {_limits.MaxMessageCharacters} characters ka rakh.");
			}

			return ValidateImage(imageData);
		}

		private string? ValidateImage(string? imageData)
		{
			if (string.IsNullOrEmpty(imageData))
			{
				return null;
			}

			string? declaredType = null;
			var encoded = imageData;
			if (imageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			{
				var separator = imageData.IndexOf(',');
				if (separator < 0)
				{
					throw InvalidImage();
				}
				var header = imageData.Substring(0, separator).ToLowerInvariant();
				if (header == "data:image/jpeg;base64")
				{
					declaredType = "image/jpeg";
				}
				else if (header == "data:image/png;base64")
				{
					declaredType = "image/png";
				}
				else
				{
					throw InvalidImage();
				}
				encoded = imageData.Substring(separator + 1);
			}

			// Request body pehle hi bounded hai; Base64 ko seedha decode karke actual size check kar.
			byte[] bytes;
			try
			{
				bytes = Convert.FromBase64String(encoded);
			}
			catch (FormatException)
			{
				throw InvalidImage();
			}
			if (bytes.Length > _limits.MaxImageBytes)
			{
				throw ImageTooLarge();
			}

			var actualType = GetImageType(bytes);
			if (declaredType != null && declaredType != actualType)
			{
				throw InvalidImage();
			}

			try
			{
				using var stream = new MemoryStream(bytes);
				var info = ImageInfo.FromStream(stream);
				if (info is null || info.Value.Width <= 0 || info.Value.Height <= 0)
				{
					throw InvalidImage();
				}
				// Chhoti compressed file bhi bahut bade pixels khol sakti hai; allocation se pehle rok.
				if ((long)info.Value.Width * info.Value.Height > _limits.MaxImagePixels)
				{
					throw new ChatInputException("Bhai, photo ke dimensions bahut bade hain. Resize karke bhej.",
						StatusCodes.Status413PayloadTooLarge);
				}
				stream.Position = 0;
				_ = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue);
			}
			catch (InvalidOperationException)
			{
				throw InvalidImage();
			}

			return $"data:{actualType};base64,{Convert.ToBase64String(bytes)}";
		}

		private static string GetImageType(byte[] bytes)
		{
			if (bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
			{
				return "image/png";
			}
			if (bytes.Take(3).SequenceEqual(new byte[] { 255, 216, 255 }))
			{
				return "image/jpeg";
			}
			throw InvalidImage();
		}

		private ChatInputException ImageTooLarge() => new(
			$"Bhai, photo {_limits.MaxImageBytes} bytes se badi hai. Compress karke bhej.",
			StatusCodes.Status413PayloadTooLarge);

		private static ChatInputException InvalidImage() =>
			new("Bhai, ek valid JPEG ya PNG photo Base64 format mein bhej.");
	}
}
