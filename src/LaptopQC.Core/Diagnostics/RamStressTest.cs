using LaptopQC.Core.Abstractions;

namespace LaptopQC.Core.Diagnostics;

/// <summary>
/// RAM stress test to verify memory stability and detect errors
/// </summary>
public class RamStressTest : IRamStressTest
{
    private readonly int _testSizeMB;
    private readonly int _iterations;
    
    public Action<ProgressInfo>? OnProgress { get; set; }
    
    public class ProgressInfo
    {
        public int PercentComplete { get; set; }
        public string Status { get; set; } = "";
    }
    
    public class RamStressResult
    {
        public bool Passed { get; set; }
        public string Message { get; set; } = "";
    }
    
    public RamStressTest(int testSizeMB = 512, int iterations = 2)
    {
        _testSizeMB = testSizeMB;
        _iterations = iterations;
    }
    
    public async Task<RamStressResult> RunAsync()
    {
        try
        {
            OnProgress?.Invoke(new ProgressInfo { PercentComplete = 0, Status = "Starting RAM test..." });
            
            for (int i = 0; i < _iterations; i++)
            {
                int progress = (i * 100) / _iterations;
                OnProgress?.Invoke(new ProgressInfo { PercentComplete = progress, Status = $"Iteration {i + 1}/{_iterations}" });
                
                // Allocate memory block
                byte[] memory = new byte[_testSizeMB * 1024 * 1024];
                
                // Write pattern
                for (int j = 0; j < memory.Length; j += 4096)
                {
                    memory[j] = (byte)(j % 256);
                }
                
                // Verify pattern
                for (int j = 0; j < memory.Length; j += 4096)
                {
                    if (memory[j] != (byte)(j % 256))
                    {
                        return new RamStressResult
                        {
                            Passed = false,
                            Message = $"Memory error detected at offset {j}"
                        };
                    }
                }
                
                // Allow GC to clean up
                memory = null!;
                GC.Collect();
                
                await Task.Delay(100);
            }
            
            OnProgress?.Invoke(new ProgressInfo { PercentComplete = 100, Status = "Complete" });
            
            return new RamStressResult
            {
                Passed = true,
                Message = $"RAM test passed ({_testSizeMB}MB x {_iterations} iterations)"
            };
        }
        catch (OutOfMemoryException)
        {
            return new RamStressResult
            {
                Passed = false,
                Message = "Out of memory - insufficient RAM available"
            };
        }
        catch (Exception ex)
        {
            return new RamStressResult
            {
                Passed = false,
                Message = $"RAM test error: {ex.Message}"
            };
        }
    }
}
