namespace Rocket.Chat.Net.Models.Results
{
    public class ErrorResult
    {
        public string Error { get; set; }

        public string Reason { get; set; }

        public string Message { get; set; }

        public string ErrorType { get; set; }
    }
}