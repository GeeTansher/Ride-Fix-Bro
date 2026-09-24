using RideFixBro.API.Configuration;
using RideFixBro.API.Services;

namespace RideFixBro.API.Tests
{
	public class ChatInputValidatorTests
	{
		[Fact]
		public void AcceptsTwoThousandCharactersAndRejectsOneMore()
		{
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			Assert.Null(validator.Validate("session", new string('a', 2000), null));
			Assert.Equal(400, Assert.Throws<ChatInputException>(
				() => validator.Validate("session", new string('a', 2001), null)).StatusCode);
		}

		[Fact]
		public void SessionIdCannotBeEmptyOrUnbounded()
		{
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			Assert.Null(validator.Validate(new string('a', 128), "Hi", null));
			Assert.Throws<ChatInputException>(() => validator.Validate(new string('a', 129), "Hi", null));
			Assert.Throws<ChatInputException>(() => validator.Validate(" ", "Hi", null));
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void DetectsPngAndJpegAndNormalizesDataUris(bool png)
		{
			var encoded = png ? Convert.ToBase64String(ImageFixtures.Png()) : ImageFixtures.JpegBase64;
			var mimeType = png ? "image/png" : "image/jpeg";
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			var expected = $"data:{mimeType};base64,{encoded}";

			Assert.Equal(expected, validator.Validate("session", "Photo", encoded));
			Assert.Equal(expected, validator.Validate("session", "Photo", expected));
			Assert.Equal(expected, validator.Validate("session", "Photo", $" \r\n{encoded}\n"));
		}

		[Theory]
		[InlineData("not base64")]
		[InlineData("aW1hZ2U=")]
		[InlineData("data:image/png;base64,")]
		[InlineData("data:image/png;base64")]
		[InlineData("data:image/gif;base64,R0lGODlh")]
		[InlineData("data:image/svg+xml;base64,PHN2Zz4=")]
		public void RejectsMalformedOrUnsupportedImages(string image)
		{
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			Assert.Equal(400, Assert.Throws<ChatInputException>(
				() => validator.Validate("session", "Photo", image)).StatusCode);
		}

		[Fact]
		public void RejectsSpoofedMimeTypeAndTruncatedPng()
		{
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			var image = ImageFixtures.Png();
			Assert.Throws<ChatInputException>(() => validator.Validate("session", "Photo",
				$"data:image/jpeg;base64,{Convert.ToBase64String(image)}"));
			Assert.Throws<ChatInputException>(() => validator.Validate("session", "Photo",
				Convert.ToBase64String(image[..40])));
		}

		[Fact]
		public void AcceptsExactlyTwoMebibytesAndRejectsOneExtraDecodedByte()
		{
			var limits = new ChatLimitsOptions();
			var validator = new ChatInputValidator(limits);
			var image = ImageFixtures.Png(2 * 1024 * 1024);
			Assert.Equal(2 * 1024 * 1024, image.Length);
			Assert.NotNull(validator.Validate("session", "Photo", Convert.ToBase64String(image)));

			var tooLarge = ImageFixtures.Png(2 * 1024 * 1024 + 1);
			Assert.Equal(413, Assert.Throws<ChatInputException>(
				() => validator.Validate("session", "Photo", Convert.ToBase64String(tooLarge))).StatusCode);
		}

		[Fact]
		public void RejectsHugePixelDimensionsBeforeDecodingPixelData()
		{
			var validator = new ChatInputValidator(new ChatLimitsOptions());
			var image = ImageFixtures.PngWithOversizedDimensions();
			Assert.Equal(413, Assert.Throws<ChatInputException>(
				() => validator.Validate("session", "Photo", Convert.ToBase64String(image))).StatusCode);
		}

		[Fact]
		public void RejectsInvalidConfigurationRatherThanDisablingLimits()
		{
			var limits = new ChatLimitsOptions { MaxToolCallsPerRequest = 0 };
			Assert.Throws<ArgumentOutOfRangeException>(() => limits.Validate());
		}
	}
}
