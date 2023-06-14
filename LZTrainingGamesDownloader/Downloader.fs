namespace LC0Training
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
open Spectre.Console

module GamesDownloader =
    
    // Define a custom exception types with a string argument
    exception FileDownloadError of string
    
    exception OversizedFileError of string

    type DownloadedFile = { FileURL : string; TargetFileName : string; ExpectedSize : int64; CurrentSize: int64; Desc: string }
      with
        member this.Name = FileInfo(this.TargetFileName).Name
        static member Empty = {FileURL = ""; TargetFileName = ""; ExpectedSize = 0L; CurrentSize = 0L; Desc= "Empty"}
    
    type DownloadPlan = 
      {
        StartDate: DateTime
        DurationInDays: int
        Url: string
        TargetDir: string
        MaxDownloads: int
        AutomaticRetries: bool
        AllowToDeleteFailedFiles : bool
      }
    
    let rnd = Random()
    let consoleLock = obj()

    let startProgressUpdate = 
      let settings = AnsiConsoleSettings()
      let console = AnsiConsole.Create(settings)
      let prog = console.Progress()
      let columns : ProgressColumn array =
        [|
          TaskDescriptionColumn()
          ProgressBarColumn()
          PercentageColumn()
          RemainingTimeColumn()
          TransferSpeedColumn()
          //SpinnerColumn()
        |]
      prog.Columns(columns)
    
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

    let downloadFileTaskAsyncWithProgressUpdate downloadFile (plan:DownloadPlan) (cts:CancellationTokenSource) (pTask:ProgressTask)  : Async<Result<_,_>> = async {
      try 
        try 
          use client =  new HttpClient()
          //let startTime = Stopwatch.GetTimestamp()
          use request = new HttpRequestMessage(HttpMethod.Get, downloadFile.FileURL)
          let fileInfo = new FileInfo(downloadFile.TargetFileName)
          if fileInfo.Exists then
              if fileInfo.Length > downloadFile.ExpectedSize then
                raise (OversizedFileError $"{fileInfo.Name} cancelled: file size bigger than expected (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})")                 
              request.Headers.Range <- RangeHeaderValue(fileInfo.Length, Nullable())

          use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token) |> Async.AwaitTask
          let totalBytes = response.Content.Headers.ContentLength
          let sizeLeftToDownload = if totalBytes.HasValue then totalBytes.Value else 0
          let sizeOnDisk = if fileInfo.Exists then fileInfo.Length else 0
          let sum = sizeLeftToDownload + sizeOnDisk
          if downloadFile.ExpectedSize <> sum then
            if sizeOnDisk > downloadFile.ExpectedSize then
              let! size = getFileSizeFromUrl client downloadFile        
              if sizeOnDisk > size then          
                raise (OversizedFileError $"{fileInfo.Name} - file size bigger than expected (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})")                 
        
          // Check if the response is successful 
          if response.IsSuccessStatusCode then
            if not totalBytes.HasValue then           
              raise (FileDownloadError $"{fileInfo.Name} - download request returns 0 bytes (current={downloadFile.CurrentSize} expected={downloadFile.ExpectedSize})")
          
            // Get the http response content stream asynchronously 
            use! contentStream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask

            // Create a file stream to save the content stream 
            use fileStream = 
              if fileInfo.Exists then 
                  File.Open(downloadFile.TargetFileName, FileMode.Append) 
              else 
                File.Create(downloadFile.TargetFileName)
          
            pTask.StartTask()
            let fileInfo = new FileInfo(downloadFile.TargetFileName)          
            let buffer = Array.zeroCreate<byte> 2048 
            let mutable finished = false
            let startBytes = fileStream.Length //if fileInfo.Exists then fileInfo.Length else 0L
            let mutable totalBytesRead = startBytes
            //let totalSize = formatFileSize downloadFile.ExpectedSize
            let mutable updateCounter = 0
          
            while not finished do
              if cts.Token.IsCancellationRequested then
                finished <- true
              updateCounter <- updateCounter + 1
              let bytesRead = contentStream.Read (buffer, 0, buffer.Length)
              if bytesRead = 0 then
                pTask.Value <- float fileStream.Length
                do! Async.Sleep 1000
                pTask.StopTask()
                finished <- true
              else
                fileStream.Write (buffer, 0, bytesRead)
                totalBytesRead <- totalBytesRead + int64 bytesRead
              if updateCounter % 1000 = 1 then
                if fileStream.Length > downloadFile.ExpectedSize then
                  raise (OversizedFileError $"{fileInfo.Name} - File oversized")
                //let percentage = (float fileStream.Length / float downloadFile.ExpectedSize) * 100.
                //pTask.MaxValue <- 100.
                pTask.Value <- float fileStream.Length // (fileStream.Length - startBytes)
                pTask.MaxValue <- float downloadFile.ExpectedSize //(downloadFile.ExpectedSize - startBytes)
            
            return Result.Ok downloadFile
          else
            return Result.Error $"Failed to start download with the following status code: {response.StatusCode}" 
                
          with
          |OversizedFileError msg -> return Result.Error $"{msg}" //Console.WriteLine(msg)
          |FileDownloadError msg -> return Result.Error $"{msg}" //Console.WriteLine($"{downloadFile.TargetFileName} download error: {msg}")
          |ex -> 
            if cts.Token.CanBeCanceled then
              cts.Cancel()
            return Result.Error $"{ex.Message}"
      finally
        pTask.StopTask()   }


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
        let fileInfo = FileInfo(file.TargetFileName)
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
          let desc = sprintf "%s failed verification: current size=%s,  expected size=%s" fileInfo.Name (formatFileSize fileInfo.Length ) (formatFileSize downloadFile.ExpectedSize )
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
        let cts = new CancellationTokenSource()
        async {
          let filesToDownload = newFiles         
          if plan.MaxDownloads < filesToDownload.Length then
             Console.WriteLine $"Got {filesToDownload.Length} files to download for the specified period, will download in chunks upto {plan.MaxDownloads} files per session";
          else
            Console.WriteLine $"Got {filesToDownload.Length} files to download for the specified period";
          
          try      
            // create chunks of download tasks
            let chunks = filesToDownload |> Seq.chunkBySize plan.MaxDownloads
            for chunk in chunks do
              let sessionStart = Stopwatch.GetTimestamp()              
              let pTask = startProgressUpdate.StartAsync(fun ctx -> 
                let tasks = ResizeArray<Async<Result<_,_>>>()  
                for downloadFile in chunk do
                  let size = formatFileSize downloadFile.ExpectedSize
                  let ptask = ctx.AddTask($"[green]{downloadFile.Name} ({size})[/]")
                  let asynctask = downloadFileTaskAsyncWithProgressUpdate downloadFile plan cts ptask
                  tasks.Add asynctask
                task {return! tasks |> Async.Parallel  } )
                
              let! results = pTask |> Async.AwaitTask
              
              //debugging
              let errors = results |> Array.sumBy(fun e -> match e with |Error _ -> 1 |_ -> 0)
              printfn "%s" (sprintf "Number of errors in download = %d" errors)
              do! Async.Sleep 10000
              let resultOption =
                results
                |>Array.tryFind (fun e -> 
                    match e with 
                    |Ok _ -> false 
                    |Error _ -> true)
              match resultOption with
              |None -> printfn $"{Environment.NewLine}All files sucessfully downloaded"
              |Some r ->
                match r with
                |Error error -> printfn $"{Environment.NewLine}Error in download: {error}"
                |_ -> () 

              let ts = Stopwatch.GetElapsedTime(sessionStart)              
              Console.Write Environment.NewLine
              let msg = sprintf "Downloaded %d files (in chunk) in %.1f minutes" (chunk |> Seq.length) ts.TotalMinutes
              Console.WriteLine msg
              
            let ts = Stopwatch.GetElapsedTime(start)            
            let msg = sprintf "Downloaded in total %d files in %f minutes and %d seconds - will verify next" (filesToDownload.Length) ts.TotalMinutes ts.Seconds
            Console.Write Environment.NewLine
            Console.WriteLine msg

          with 
          | :? InvalidOperationException as e -> 
            Console.WriteLine($"Invalid operation {e.Message}")
          | :? HttpRequestException as e ->
            Console.WriteLine $"Http request failure: {e.Message}"
          | :? OperationCanceledException as e  ->
            // Handle the cancellation exception            
            Console.WriteLine $"Download cancelled: {e.Message}"
          | FileDownloadError msg -> 
            Console.WriteLine $"Download error: {msg}"
          | ex ->
            // Handle any other exception
              failwith $"Program failed in download: {ex.Message}" 
        }

   