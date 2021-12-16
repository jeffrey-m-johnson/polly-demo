using System.Diagnostics;
using Polly;
using Polly.Wrap;
using Polly.CircuitBreaker;

Stopwatch stopwatch = new Stopwatch();
void LogMessage(string message)
{
    var elapsedTime = stopwatch.Elapsed;
    string timestamp = string.Format("{0:D2}m:{1:D2}s:{2:D3}ms", elapsedTime.Minutes, elapsedTime.Seconds, elapsedTime.Milliseconds);
    Console.WriteLine($"[{timestamp}]: {message}");
}

Random random = new Random();
TimeSpan RetryThrottleProvider(int retryCounter)
{
    TimeSpan waitTime = TimeSpan.FromSeconds(0.1 * Math.Pow(2, retryCounter)); // Exponential backoff
    TimeSpan jitterTime = TimeSpan.FromMilliseconds(random.Next(0, 300)); // Add a little variation in throttle time to avoid multiple retries happening at once
    TimeSpan totalThrottleTime = waitTime + jitterTime;
    return totalThrottleTime;
}

void OnRetry(Exception exception, TimeSpan timespan, int retryCount, Context context)
{
    // Log when a retry occurs
    LogMessage($"Retrying...");
}

// This policy defines the retry behavior.
// Retry policies are useful for handling transient errors, i.e.: errors that are unpredictable such as network congestion or service outages.
// With these sorts of errors sometimes the issue can be resolved simply by retrying the action after a short period of time.
var retryPolicy = Policy
    // The policy applies only when this sort of exception is thrown.
    // Using the base-class <Exception> means the retry policy applies to all exceptions
    .Handle<Exception>(
        exception => !(exception is BrokenCircuitException)) // Don't retry circuit breaker exceptions
    .WaitAndRetryAsync(
        // Number of times to retry the action
        retryCount: 3,

        // How long to wait between retries.
        // A simple TimeSpan can be used here, but it is better to have the duration be a function of the number of
        // times the action has been attempted, i.e. exponential backoff
        sleepDurationProvider: RetryThrottleProvider,

        // Optional: Action to call when a retry occurs
        onRetry: OnRetry
    );

var fallbackPolicy = Policy
    .Handle<Exception>()
    .FallbackAsync((cancellationToken) => {
        LogMessage("! Fallback Action");
        return Task.CompletedTask;
    }, (ex) => {
        LogMessage($"{ex.GetType()}");
        return Task.CompletedTask;
    });

// This policy defines the circuit breaker behavior.
// Circuit breaker policies are useful for
var circuitBreakerPolicy = Policy
    .Handle<Exception>()
    .AdvancedCircuitBreakerAsync(
        failureThreshold: 0.25,
        samplingDuration: TimeSpan.FromSeconds(30),
        durationOfBreak: TimeSpan.FromSeconds(10),
        minimumThroughput: 64,
        onBreak: (exception, circuitState, timespan, context) =>
        {
            LogMessage($"Circuit breaker break: waiting {timespan}...");
        },
        onReset: (context) =>
        {
            LogMessage($"Circuit breaker has reset!");
        },
        onHalfOpen: () =>
        {
            LogMessage($"Circuit breaker half-open");
        }
    );

// Combine policies into one.
// !Importent: Policy order matters
AsyncPolicyWrap combinedPolicy = Policy.WrapAsync(
    fallbackPolicy,
    retryPolicy,
    circuitBreakerPolicy
);

// Simulate activity with an error rate of 1/5/
// Most often the Primary Action will execute, but there
// should be occasional retries and if enough failures occur
// close enough to eachother the circuit breaker will trip
// and the Fallback Action will execute.
// The Fallback Action will also execute if all the retries fail,
// even if the circuit breaker isn't tripped.
stopwatch = System.Diagnostics.Stopwatch.StartNew();
while (true)
{
    combinedPolicy.ExecuteAsync(async () => {
        bool isFailure = random.Next(1, 5) == 1; // 1/5 failure rate
        if (isFailure) {
            throw new Exception("Ope");
        }
        LogMessage("! Primary Action");
        await Task.CompletedTask;
    });
    Thread.Sleep(250);
}
