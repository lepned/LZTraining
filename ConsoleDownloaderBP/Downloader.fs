namespace LC0Training
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Net.Http
open System.Threading.Tasks
open System.Diagnostics

module GamesDownloader =
    type DownloadSpec = 
      {
        StartDate: DateTime
        Duration: TimeSpan
        Url: string
        TargetDir: string
        MaxDownloads: int
        AutomaticRetries: bool
        HttpClient: HttpClient
        Semaphore: SemaphoreSlim
        CTS: CancellationTokenSource
      }
      
    let downloadFileTaskAsync (fileUrl:string) targetFn (spec:DownloadSpec) = task {
      try 
        use! response = spec.HttpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, spec.CTS.Token)

        // Check if the response is successful 
        if response.IsSuccessStatusCode then

          // Get the http response content stream asynchronously 
          use! contentStream = response.Content.ReadAsStreamAsync()

          // Create a file stream to save the content stream 
          use fileStream = File.Create(targetFn)

          // Copy the content stream to the file stream asynchronously 
          do! contentStream.CopyToAsync(fileStream)

          // Report the progress or success of the download
          printfn "Downloaded %s" fileUrl
        else
          printfn "FileUrl could not be downloaded - check url and retry later"
      
      finally
        // Release the semaphore
        spec.Semaphore.Release() |> ignore }

    let getFileUrlAndTargetFn (line:string) url targetDir =
      let pieces = line.Split('"')
      let fn = pieces.[1]
      let fileURL = url + "/" + fn
      let targetFN = Path.Combine(targetDir, fn)
      (fileURL, targetFN)

    let createDateMatchStringList spec =
      let startDate = spec.StartDate
      let endDate = startDate + spec.Duration
      let days = (endDate - startDate).Days
      [ for d in 0..days-1 -> startDate.AddDays(float d).ToString("yyyyMMdd") ]
    
    let rec getFileUrlAndTargetFnList (spec : DownloadSpec) =
      task {
          try
              do! downloadFileTaskAsync spec.Url "training.htm" spec
              let matchList = createDateMatchStringList spec
              let urlAndFn = 
                File.ReadAllLines "training.htm"
                |> Array.filter(fun line -> matchList |> List.exists(fun e -> line.Contains e))
                |> Array.map(fun line -> getFileUrlAndTargetFn line spec.Url spec.TargetDir)
                |> Array.filter(fun (_, targetFN) -> not (File.Exists targetFN))

              return urlAndFn
          with
          | _ as e ->
              printfn "Download file error: %s" e.Message
              do! Async.Sleep 1000
              if spec.AutomaticRetries then
                  return! getFileUrlAndTargetFnList spec
              else
                  return [||]
      }
    
    let downloadFileAsync(fileUrl: string, targetFn:string, spec:DownloadSpec) =
        task {
          // Acquire the semaphore
          do! spec.Semaphore.WaitAsync(spec.CTS.Token)
          //printfn "semaphore acquired"
          try
            //printfn "Downloading %s" fileUrl
            do! downloadFileTaskAsync fileUrl targetFn spec

          finally
            // Release the semaphore
            spec.Semaphore.Release() |> ignore }
    
    let downloadAllFilesInChunks (spec:DownloadSpec) =
        let start = Stopwatch.GetTimestamp()
        async {
          let! filesToDownload = getFileUrlAndTargetFnList spec |> Async.AwaitTask
          if spec.MaxDownloads < filesToDownload.Length then
            printfn $"Got {filesToDownload.Length} files to download for the specified period, will download in chunks of {spec.MaxDownloads} files per session";
          else
            printfn $"Got {filesToDownload.Length} files to download for the specified period";
          //let filesToDownload = filesToDownload |> Array.truncate spec.MaxDownloads
          let downloadTasks = List<Task>()

          // Loop through the file URLs
          for (fileURL, targetFN) in filesToDownload do
            // Create a download task for each file URL
            let downloadTask = downloadFileAsync(fileURL, targetFN, spec)
            // Add the task to the list
            downloadTasks.Add(downloadTask)
            
          try
            try
              // create chunks of download tasks
              let chunks = downloadTasks |> Seq.chunkBySize spec.MaxDownloads
              for chunk in chunks do
                let sessionStart = Stopwatch.GetTimestamp()
                printfn "Downloading a chunk of %d files at a time" (chunk |> Seq.length)
                do! Task.WhenAll(chunk) |> Async.AwaitTask
                let ts = Stopwatch.GetElapsedTime(sessionStart)
                printfn "Downloaded %d files (in chunk) %f minutes and %d seconds" (chunk |> Seq.length) ts.TotalMinutes ts.Seconds
              
              let ts = Stopwatch.GetElapsedTime(start)
              printfn "Downloaded %d all files in %f minutes and %d seconds" (downloadTasks.Count) ts.TotalMinutes ts.Seconds
            with 
            | :? OperationCanceledException ->
              // Handle the cancellation exception
              printfn "Download cancelled."
            | ex ->
              // Handle any other exception
              printfn "Download failed: %s" ex.Message
          finally
            // Dispose the http client, semaphore, and cancellation token source
            spec.HttpClient.Dispose()
            spec.Semaphore.Dispose()
            spec.CTS.Dispose()
        }
    
    let startDownloadSessionAsync (spec:DownloadSpec) =
        let start = Stopwatch.GetTimestamp()
        async {
          let! filesToDownload = getFileUrlAndTargetFnList spec |> Async.AwaitTask
          if spec.MaxDownloads < filesToDownload.Length then
            printfn $"Got {filesToDownload.Length} files to download for the specified period, but max downloads is set to {spec.MaxDownloads} files per session";
          else
            printfn $"Got {filesToDownload.Length} files to download for the specified period";
          let filesToDownload = filesToDownload |> Array.truncate spec.MaxDownloads
          let downloadTasks = List<Task>()

          // Loop through the file URLs
          for (fileURL, targetFN) in filesToDownload do
            // Create a download task for each file URL
            let downloadTask = downloadFileAsync(fileURL, targetFN, spec)
            // Add the task to the list
            downloadTasks.Add(downloadTask)
            
          try
            try
              // Wait for all the tasks to complete or cancel
              do! Task.WhenAll(downloadTasks) |> Async.AwaitTask
              let ts = Stopwatch.GetElapsedTime(start)
              printfn "Downloaded %d files in %f minutes and %d seconds" (downloadTasks.Count) ts.TotalMinutes ts.Seconds
            with 
            | :? OperationCanceledException ->
              // Handle the cancellation exception
              printfn "Download cancelled."
            | ex ->
              // Handle any other exception
              printfn "Download failed: %s" ex.Message
          finally
            // Dispose the http client, semaphore, and cancellation token source
            spec.HttpClient.Dispose()
            spec.Semaphore.Dispose()
            spec.CTS.Dispose()
        }


    