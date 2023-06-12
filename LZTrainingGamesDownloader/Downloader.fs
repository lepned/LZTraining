﻿namespace LC0Training
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Threading
open System.Net.Http
open System.Threading.Tasks
open System.Diagnostics
open System.Text.RegularExpressions
open System.Net.Http.Headers

module GamesDownloader =
    
    type DownloadedFile = { FileURL : string; TargetFileName : string; ExpectedSize : int64; CurrentSize: int64; Desc: string }
      with
        static member Empty = {FileURL = ""; TargetFileName = ""; ExpectedSize = 0L; CurrentSize = 0L; Desc= "Empty"}
    
    type DownloadPlan = 
      {
        StartDate: DateTime
        DurationInDays: int
        Url: string
        TargetDir: string
        MaxDownloads: int
        AutomaticRetries: bool
        EnableProgressUpdate : bool
        AllowToDeleteFailedFiles : bool
        CTS: CancellationTokenSource
      }
    
    let consoleLock = obj()

    // Keep track of the last line written
    let mutable lastLineWritten = 0
    
    let simpleSafeProgressUpdate () =
      let dict = new Dictionary<string, int>()
      let mutable maxV = 0
      
      fun (file:DownloadedFile) (msg:string) -> 
        //if not keep then
        //  dict.Remove (file.TargetFileName) |> ignore
        lock consoleLock (fun () -> 
          if (dict.Count = 0) then
            Console.WriteLine("************************************")
          
          if dict.ContainsKey file.TargetFileName |> not then
            Console.Write Environment.NewLine //Console.WriteLine("***")
            let struct (_,top) = Console.GetCursorPosition()
            let allValues = dict.Values |> Seq.toArray
            if allValues.Length > 1 && allValues |> Array.exists (fun x -> x = top) then
              Console.Write Environment.NewLine
              dict.Add(file.TargetFileName, top + 1)
            else
              dict.Add(file.TargetFileName, top)
          let line = dict[file.TargetFileName] 
          let struct (_,t) = Console.GetCursorPosition()
          maxV <- max maxV t
          
          let progressUpdate = 
            if msg = "" then
              sprintf "%s %d/%d" file.TargetFileName file.CurrentSize file.ExpectedSize
            else
              msg
          if t <> line then
            Console.SetCursorPosition(0, line)
            Console.Write progressUpdate
            Console.SetCursorPosition(0, t)
          else
            Console.Write progressUpdate
          maxV   
          )
    
    let writeEmptyLine() =
      Console.WriteLine(new string(' ', Console.WindowWidth - 1))
    
    let printConsoleValues() =
      let ct = Console.CursorTop
      let cl = Console.CursorLeft
      let wh = Console.WindowHeight
      let ww = Console.WindowWidth
      let wt = Console.WindowTop
      let wl = Console.WindowLeft
      let bh = Console.BufferHeight
      let bw = Console.BufferWidth
      let struct (w,t) = Console.GetCursorPosition()
      let lh = Console.LargestWindowHeight
      let lw = Console.LargestWindowWidth
      printfn "Window: wh=%d ww=%d wt=%d wl=%d, buffer: bh=%d bw=%d, Cursor: cl=%d ct=%d, cursorPos: w=%d t=%d, Largest: lh=%d lw=%d" wh ww wt wl bh bw cl ct w t lh lw

    // Create function to perform thread-safe console writing
    let threadSafeWrite (top: int) (text:string) = 
        lock consoleLock (fun () ->            
          Console.BufferWidth <- 1000
          Console.BufferHeight <- 1000
          Console.SetCursorPosition(0, top)
          Console.Write (text)
            
          // Update the last line written
          if top > lastLineWritten then
            lastLineWritten <- top
          let struct (_,t) = Console.GetCursorPosition()          
          Console.CursorTop <- max top t
          Console.CursorLeft <- 0
        )


    let getFileSize text =
      let pattern = @"\s(\d+)$" // pattern for one or more digits at the end of the string
      let test = Regex.Match(text, pattern)
      if test.Success then
          let numberString = test.Groups.[1].Value // get the first group
          let number = int64 numberString // convert the string to an integer
          Some number          
      else
          None
    
    let formatTimeSpan (timeSpan: TimeSpan) =
      (new TimeSpan(0, timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds)).ToString("c")
    
    let rec formatFileSize (fileSize: int64) =
      let units = [|"bytes"; "KB"; "MB"; "GB"|]

      let rec format size unit =
          if size >= 1024.0 && unit < units.Length - 1 then format (size / 1024.0) (unit + 1)
          else sprintf "%.2f %s" size units.[unit]

      fileSize |> float |> (fun size -> format size 0)


    let reportProgress totalBytesRead contentLength = 
      let percentage = float totalBytesRead / float contentLength * 100.0
      printfn "Downloaded %A%%" percentage

    let downloadMainFileAsync (url:string) = async {
      try 
        use client =  new HttpClient()
        use! response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
        
        // Check if the response is successful 
        if response.IsSuccessStatusCode then
          
          // Get the http response content stream asynchronously 
          use! contentStream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask

          // Create a file stream to save the content stream 
          use fileStream = File.Create("training.htm")

          // Copy the content stream to the file stream asynchronously 
          do! contentStream.CopyToAsync(fileStream) |> Async.AwaitTask            
         
        else
          printfn "Main file could not be downloaded - check URL and retry later"
      
      with
      |_ as e -> printfn "Download file error: %s" e.Message  }    

    let getFileSizeFromUrl (client:HttpClient) (downloadFile:DownloadedFile) = async {
      use! response = client.GetAsync(downloadFile.FileURL, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
      let totalBytes = response.Content.Headers.ContentLength
      let size = if totalBytes.HasValue then totalBytes.Value else 0L
      return size }

    let downloadFileTaskAsync downloadFile (plan:DownloadPlan) = async {
      try 
        use client =  new HttpClient()
        let startTime = Stopwatch.GetTimestamp()
        use request = new HttpRequestMessage(HttpMethod.Get, downloadFile.FileURL)
        let fileInfo = new FileInfo(downloadFile.TargetFileName)
        if fileInfo.Exists then
            if fileInfo.Length > downloadFile.ExpectedSize then
              failwith $"{fileInfo.Name} - file size bigger than expected (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"                  
            request.Headers.Range <- RangeHeaderValue(fileInfo.Length, Nullable())

        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, plan.CTS.Token) |> Async.AwaitTask
        let totalBytes = response.Content.Headers.ContentLength
        let sizeLeftToDownload = if totalBytes.HasValue then totalBytes.Value else 0
        let sizeOnDisk = if fileInfo.Exists then fileInfo.Length else 0
        let sum = sizeLeftToDownload + sizeOnDisk
        if downloadFile.ExpectedSize <> sum then
          if sizeOnDisk > downloadFile.ExpectedSize then
            let! size = getFileSizeFromUrl client downloadFile        
            if sizeOnDisk > size then          
              failwith $"Something wrong with the file {downloadFile.TargetFileName} - consider deleting the file and rerun"
        
        // Check if the response is successful 
        if response.IsSuccessStatusCode then
          if not totalBytes.HasValue then           
            failwith $"{fileInfo.Name} - download request returns 0 bytes (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"
          
          // Get the http response content stream asynchronously 
          use! contentStream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
          let msg = sprintf "%s - Download started (file size = %s) " fileInfo.Name (formatFileSize downloadFile.ExpectedSize)
          Console.WriteLine msg
          // Create a file stream to save the content stream 
          use fileStream = 
            if fileInfo.Exists then 
                File.Open(downloadFile.TargetFileName, FileMode.Append) 
            else 
              File.Create(downloadFile.TargetFileName)

          do! contentStream.CopyToAsync(fileStream) |> Async.AwaitTask
          do! Async.Sleep 1000
          let fileHandle = new FileInfo(downloadFile.TargetFileName)
          let bytesOnDisk = fileStream.Length
          let bytesOnDiskFormatted = formatFileSize bytesOnDisk
          let dur = Stopwatch.GetElapsedTime startTime
          let time = formatTimeSpan dur
          let percentage = (float bytesOnDisk / float downloadFile.ExpectedSize) * 100.
          let totalSize = formatFileSize downloadFile.ExpectedSize
          let kbps = (float bytesOnDisk / 1024.) / dur.TotalSeconds
          let msg = sprintf "%s - Downloaded %s of %s (%.2f%%) in %s kbps=%.0f          " fileHandle.Name bytesOnDiskFormatted totalSize percentage time kbps
          Console.WriteLine msg
        else
          Console.WriteLine "Download error"
        return downloadFile
      
        with
        |_ as e -> failwith $"Download error for: {e.Message} "; return downloadFile }

    let downloadFileTaskAsyncWithProgress line downloadFile (spec:DownloadPlan) = async {
      try 
        use client =  new HttpClient()
        let startTime = Stopwatch.GetTimestamp()
        use request = new HttpRequestMessage(HttpMethod.Get, downloadFile.FileURL)
        let fileInfo = new FileInfo(downloadFile.TargetFileName)
        if fileInfo.Exists then
            if fileInfo.Length > downloadFile.ExpectedSize then
              failwith $"{fileInfo.Name} - file size bigger than expected (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"                  
            request.Headers.Range <- RangeHeaderValue(fileInfo.Length, Nullable())

        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, spec.CTS.Token) |> Async.AwaitTask
        let totalBytes = response.Content.Headers.ContentLength
        let sizeLeftToDownload = if totalBytes.HasValue then totalBytes.Value else 0
        let sizeOnDisk = if fileInfo.Exists then fileInfo.Length else 0
        let sum = sizeLeftToDownload + sizeOnDisk
        if downloadFile.ExpectedSize <> sum then
          if sizeOnDisk > downloadFile.ExpectedSize then
            let! size = getFileSizeFromUrl client downloadFile        
            if sizeOnDisk > size then          
              failwith $"Something wrong with the file {downloadFile.TargetFileName} - consider deleting the file and rerun"
        
        // Check if the response is successful 
        if response.IsSuccessStatusCode then
          if not totalBytes.HasValue then           
            failwith $"{fileInfo.Name} - download request returns 0 bytes (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"
          
          // Get the http response content stream asynchronously 
          use! contentStream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask

          // Create a file stream to save the content stream 
          use fileStream = 
            if fileInfo.Exists then 
                File.Open(downloadFile.TargetFileName, FileMode.Append) 
            else 
              File.Create(downloadFile.TargetFileName)

          let fileInfo = new FileInfo(downloadFile.TargetFileName)          
          let buffer = Array.zeroCreate<byte> 2048 
          let mutable finished = false
          let bytesSofar = if fileInfo.Exists then fileInfo.Length else 0L
          let mutable totalBytesRead = bytesSofar
          let totalSize = formatFileSize downloadFile.ExpectedSize
          let mutable updateCounter = 0
          
          let writeProgress() =
            let dur = Stopwatch.GetElapsedTime startTime
            let time = formatTimeSpan dur
            let fileSizeRead = formatFileSize totalBytesRead
            let percentage = (float totalBytesRead / float downloadFile.ExpectedSize) * 100.
            let kbps = (float (totalBytesRead-bytesSofar) / 1024.) / dur.TotalSeconds
            let msg = sprintf "\r%s - Downloaded %s of %s (%.2f%%) in %s kbps=%.0f          " fileInfo.Name fileSizeRead totalSize percentage time kbps
            threadSafeWrite line msg

          while not finished do
            updateCounter <- updateCounter + 1
            let bytesRead = contentStream.Read (buffer, 0, buffer.Length)
            if bytesRead = 0 then 
              writeProgress()
              finished <- true
            else
              fileStream.Write (buffer, 0, bytesRead)
              totalBytesRead <- totalBytesRead + int64 bytesRead
            if updateCounter % 1000 = 1 then
              writeProgress()
          
        return downloadFile
      
        with
        |_ as e -> failwith $"Download error for: {e.Message} "; return downloadFile }

    let downloadFileTaskAsyncWithProgressUpdate downloadFile (plan:DownloadPlan) update = async {
      try 
        use client =  new HttpClient()
        let startTime = Stopwatch.GetTimestamp()
        use request = new HttpRequestMessage(HttpMethod.Get, downloadFile.FileURL)
        let fileInfo = new FileInfo(downloadFile.TargetFileName)
        if fileInfo.Exists then
            if fileInfo.Length > downloadFile.ExpectedSize then
              failwith $"{fileInfo.Name} - file size bigger than expected (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"                  
            request.Headers.Range <- RangeHeaderValue(fileInfo.Length, Nullable())

        use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, plan.CTS.Token) |> Async.AwaitTask
        let totalBytes = response.Content.Headers.ContentLength
        let sizeLeftToDownload = if totalBytes.HasValue then totalBytes.Value else 0
        let sizeOnDisk = if fileInfo.Exists then fileInfo.Length else 0
        let sum = sizeLeftToDownload + sizeOnDisk
        if downloadFile.ExpectedSize <> sum then
          if sizeOnDisk > downloadFile.ExpectedSize then
            let! size = getFileSizeFromUrl client downloadFile        
            if sizeOnDisk > size then          
              failwith $"Something wrong with the file {downloadFile.TargetFileName} - consider deleting the file and rerun"
        
        // Check if the response is successful 
        if response.IsSuccessStatusCode then
          if not totalBytes.HasValue then           
            failwith $"{fileInfo.Name} - download request returns 0 bytes (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})"
          
          // Get the http response content stream asynchronously 
          use! contentStream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask

          // Create a file stream to save the content stream 
          use fileStream = 
            if fileInfo.Exists then 
                File.Open(downloadFile.TargetFileName, FileMode.Append) 
            else 
              File.Create(downloadFile.TargetFileName)

          let fileInfo = new FileInfo(downloadFile.TargetFileName)          
          let buffer = Array.zeroCreate<byte> 2048 
          let mutable finished = false
          let bytesSofar = if fileInfo.Exists then fileInfo.Length else 0L
          let mutable totalBytesRead = bytesSofar
          let totalSize = formatFileSize downloadFile.ExpectedSize
          let mutable updateCounter = 0
          
          let sendProgress() =
            let dur = Stopwatch.GetElapsedTime startTime
            let time = formatTimeSpan dur
            let fileSizeRead = formatFileSize totalBytesRead
            let percentage = (float totalBytesRead / float downloadFile.ExpectedSize) * 100.
            let kbps = (float (totalBytesRead-bytesSofar) / 1024.) / dur.TotalSeconds
            let msg = sprintf "\r%s - Downloaded %s of %s (%.2f%%) in %s kbps=%.0f          " fileInfo.Name fileSizeRead totalSize percentage time kbps
            let line : int = update downloadFile msg
            line

          while not finished do
            updateCounter <- updateCounter + 1
            let bytesRead = contentStream.Read (buffer, 0, buffer.Length)
            if bytesRead = 0 then 
              let line = sendProgress()
              //Console.SetCursorPosition(0, line)
              finished <- true
            else
              fileStream.Write (buffer, 0, bytesRead)
              totalBytesRead <- totalBytesRead + int64 bytesRead
            if updateCounter % 100 = 1 then
              sendProgress() |> ignore
          
        return downloadFile
      
        with
        |_ as e -> failwith $"Download error for: {e.Message} "; return downloadFile }


    let getFileUrlAndTargetFn (line:string) url targetDir =
      let pieces = line.Split('"')
      let fn = pieces.[1]
      let fileURL = url + "/" + fn
      let targetFN = Path.Combine(targetDir, fn)
      let size = match getFileSize line with |Some n -> n |_ -> 0
      let file = { FileURL = fileURL; TargetFileName = targetFN; ExpectedSize = size; CurrentSize = 0; Desc = ""  }
      file

    let createDateMatchStringList plan =
      let startDate = plan.StartDate
      let endDate = startDate + TimeSpan.FromDays(plan.DurationInDays)
      let days = (endDate - startDate).Days
      [ for d in 0..days-1 -> startDate.AddDays(float d).ToString("yyyyMMdd") ]
    

    let rec getFileUrlAndTargetFnList (plan : DownloadPlan) =
      async {
          try
              do! downloadMainFileAsync plan.Url
              let matchList = createDateMatchStringList plan
              let urlAndFn = 
                File.ReadAllLines "training.htm"
                |> Array.filter(fun line -> matchList |> List.exists(fun e -> line.Contains e))
                |> Array.map(fun line -> getFileUrlAndTargetFn line plan.Url plan.TargetDir)

              return urlAndFn
          with
          | _ as e ->
              printfn "Download file error: %s" e.Message
              do! Async.Sleep 1000
              if plan.AutomaticRetries then
                  return! getFileUrlAndTargetFnList plan
              else
                  return [||]
      }
    
    let createVerificationSummary plan (name:string option) =      
      let periodStart = plan.StartDate.ToString("yyyyMMdd")
      let periodEnd = (plan.StartDate + TimeSpan.FromDays(plan.DurationInDays)).ToString("yyyyMMdd")
      let fileName = sprintf "File_summary_From_%s_To_%s" periodStart periodEnd
      let fileName = defaultArg name fileName
      let files = getFileUrlAndTargetFnList plan |> Async.RunSynchronously
      let sb = System.Text.StringBuilder()
      sb.AppendLine (sprintf "Number of files=%d\n" files.Length) |> ignore
      let mutable passed = true
      let client = new HttpClient()
      for file in files do
        let fileInfo = System.IO.FileInfo(file.TargetFileName)
        let actualSize = 
          if fileInfo.Exists then
            let length = fileInfo.Length
            if length <> file.ExpectedSize then
              if length > file.ExpectedSize then
                let doublecheck = getFileSizeFromUrl client file |> Async.RunSynchronously
                if doublecheck <> length then 
                  if plan.AllowToDeleteFailedFiles then
                    fileInfo.Delete()
                  passed <- false
              else
                passed <- false
            length
          else
            passed <- false
            0L
        let actSize = formatFileSize actualSize
        let expectedSize = formatFileSize file.ExpectedSize
        let msg = sprintf "%s - actual size / expected size: %s / %s Diff=%d" fileInfo.Name actSize expectedSize (file.ExpectedSize-actualSize)
        sb.AppendLine msg |> ignore
      let path = Path.Combine(Environment.CurrentDirectory, fileName)
      File.WriteAllText(path, sb.ToString())      
      passed

    let getFileSizeFromResponsHeaderAsyncForAllFiles (plan : DownloadPlan) = async {
      try 
        let! filesToDownload = getFileUrlAndTargetFnList plan                
        use client =  new HttpClient()
        for file in filesToDownload do         
          use! response = client.GetAsync(file.FileURL, HttpCompletionOption.ResponseHeadersRead) |> Async.AwaitTask
          let totalBytes = response.Content.Headers.ContentLength
          let size = if totalBytes.HasValue then totalBytes.Value else 0L
          printfn "File size for file %s is: %s" file.FileURL (formatFileSize size)
      finally
        printfn "Done with checking file sizes for all files"  }

    //File might already exists. Checking if it matches expected size..."
    let inspectFile (downloadFile:DownloadedFile)  =
      let fileInfo = new FileInfo(downloadFile.TargetFileName)        
      if fileInfo.Exists then
        if fileInfo.Length = downloadFile.ExpectedSize then
            None        
        else
          let desc = sprintf "File failed verification: current size=%s,  expected size=%s" (formatFileSize fileInfo.Length ) (formatFileSize downloadFile.ExpectedSize )
          let failedFile = { downloadFile with CurrentSize = fileInfo.Length; Desc = desc }
          Some failedFile
      else 
        None    
    
    let collectAllFilesThatNeedToBeResumed (plan : DownloadPlan) = async {      
      try 
        let! filesToDownload = getFileUrlAndTargetFnList plan 
        let filtered = 
          filesToDownload 
          |> Array.map inspectFile
          |> Array.choose id
        return filtered
      finally () }

    let collectAllNewFiles (plan : DownloadPlan) = async {      
      try 
        let! filesToDownload = getFileUrlAndTargetFnList plan 
        let filtered = 
          filesToDownload 
          |> Array.filter(fun file -> not (File.Exists file.TargetFileName))
        return filtered
      finally () }

   
    let downloadFilesInChunks (newFiles: DownloadedFile array) (plan:DownloadPlan) =
        System.Console.CursorVisible <- false
        let start = Stopwatch.GetTimestamp()
        async {
          let filesToDownload = newFiles         
          if plan.MaxDownloads < filesToDownload.Length then
             Console.WriteLine $"Got {filesToDownload.Length} files to download for the specified period, will download in chunks of {plan.MaxDownloads} files per session";
          else
            Console.WriteLine $"Got {filesToDownload.Length} files to download for the specified period";
            
          try
            try
              
              if plan.EnableProgressUpdate && Console.CursorTop > lastLineWritten then
                lastLineWritten <- Console.CursorTop                
              let limit = Math.Min(plan.MaxDownloads,10)
              // create chunks of download tasks
              let chunks = filesToDownload |> Seq.chunkBySize limit
              for chunk in chunks do
                let sessionStart = Stopwatch.GetTimestamp()
                let msg = sprintf "Downloading a chunk of %d files at a time - this will take some time..." (chunk |> Seq.length)                  
                Console.WriteLine msg                
                let update = simpleSafeProgressUpdate ()
                let mutable lineNr = lastLineWritten + 2 
                let downloadTasks = List<Async<DownloadedFile>>()
                for downloadFile in chunk do
                  let downloadTask = 
                    if plan.EnableProgressUpdate then
                      writeEmptyLine()
                      lineNr <- lineNr + 1
                      downloadFileTaskAsyncWithProgress lineNr downloadFile plan 
                    else
                      downloadFileTaskAsync downloadFile plan
                      //downloadFileTaskAsyncWithProgressUpdate downloadFile plan update
                  downloadTasks.Add(downloadTask)
                
                //run all downloads in parallel
                let! _ =
                    downloadTasks 
                    |> Async.Parallel
                
                if plan.EnableProgressUpdate && lastLineWritten > Console.CursorTop then
                  Console.CursorTop <- lastLineWritten

                let ts = Stopwatch.GetElapsedTime(sessionStart)
                Console.Write Environment.NewLine
                //Console.WriteLine "*************************************"
                let msg = sprintf "Downloaded %d files (in chunk) %.1f minutes and %d seconds" (chunk |> Seq.length) ts.TotalMinutes ts.Seconds
                Console.WriteLine msg
              
              let ts = Stopwatch.GetElapsedTime(start)
              let msg = sprintf "Downloaded %d files in %f minutes and %d seconds" (filesToDownload.Length) ts.TotalMinutes ts.Seconds
              Console.WriteLine msg

            with 
            | :? OperationCanceledException as e  ->
              // Handle the cancellation exception
              failwith $"Download cancelled: {e.Message}"
            | ex ->
              // Handle any other exception
              failwith $"Download failed: {ex.Message}" 
          finally
            () //plan.CTS.Dispose()
        }

   