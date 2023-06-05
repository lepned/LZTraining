open System
open System.Threading
open LC0Training.GamesDownloader
open System.Net.Http

let spec =
  {
    StartDate = DateTime(2022,12,01)
    Duration = TimeSpan.FromDays 1
    Url = "https://storage.lczero.org/files/training_data/test80"
    TargetDir= "C:/Dev/Chess/TrainingData"
    MaxDownloads = 25
    AutomaticRetries = true
    HttpClient = new HttpClient()
    Semaphore = new SemaphoreSlim(25)
    CTS = new CancellationTokenSource()
  }

//startDownloadSessionAsync spec |> Async.RunSynchronously

//downloadAllFilesInChunks spec |> Async.RunSynchronously

printfn "Press any key to exit..."
