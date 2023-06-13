open System
open System.IO
open System.Threading
open LC0Training.GamesDownloader
open System.Text.Json

//Download training data from LC0

let defaultPlan =  
  {
    StartDate = DateTime(2022,11,01)
    DurationInDays = 1
    Url = "https://storage.lczero.org/files/training_data/test80"
    TargetDir= "E:\LZGames\T80"
    MaxDownloads = 10 // max number of downloads is limited to 10
    AutomaticRetries = true
    AllowToDeleteFailedFiles = true
    EnableProgressUpdate = false
    CTS = new CancellationTokenSource()
  }

let continueDownload (invalidFiles: DownloadedFile array) (plan:DownloadPlan) =
  if invalidFiles.Length = 0 then
    false
  else
    printfn "Total number of oversized files = %d:" invalidFiles.Length
    for file in invalidFiles do
      printfn "%s" file.Desc
    if plan.AllowToDeleteFailedFiles then
      for file in invalidFiles do
        FileInfo(file.TargetFileName).Delete()
      printfn "All oversized files got deleted"
      true
    else
      printfn "Press [R] to remove all oversized files and restart the download. [Any other key] to end the program."                            
      let keyInfo = Console.ReadKey()
      if keyInfo.Key = ConsoleKey.R then
        printfn "Press [Enter] to confirm operation"
        let confirmation = Console.ReadKey()
        if confirmation.Key = ConsoleKey.Enter then
          for file in invalidFiles do
            FileInfo(file.TargetFileName).Delete()
          printfn "All files deleted. Download will restart for oversized files"
          true
        else
          false
      else 
        false

let verify plan =
  printfn "Verification started..."
  let passed = createVerificationSummary plan None //(Some "Dec22.txt")
  if passed then 
    printfn "Verification passed" 
  else 
    printfn "Verification failed - look at your bin dir for summary"
    let list = ResizeArray<DownloadedFile>()
    let failedResumedFiles = collectAllFilesThatNeedToBeResumed plan |> Async.RunSynchronously
    let newFiles = collectAllNewFiles plan |> Async.RunSynchronously
    list.AddRange failedResumedFiles
    list.AddRange newFiles
    printfn "Number of files that need to be downloaded=%d" list.Count
    for file in list do
      let msg = sprintf "File: %s Expected=%d OnDisk=%d" file.TargetFileName file.ExpectedSize file.CurrentSize
      printfn "%s" msg


let downloadVerificationLoop plan =    
    let mutable tries = 0
    let rec loop () = async { 
      try
        tries <- tries + 1
        let! resumedFiles = collectAllFilesThatNeedToBeResumed plan
        let (invalidFiles, normalFiles) = resumedFiles |> Array.partition (fun f -> f.CurrentSize > f.ExpectedSize)
        let! newFiles = collectAllNewFiles plan
        let sum = normalFiles.Length + newFiles.Length
        let tmpFiles = ResizeArray<DownloadedFile>()
        tmpFiles.AddRange normalFiles //resumedFiles
        tmpFiles.AddRange newFiles
        let filesToDownload = tmpFiles |> Seq.toArray |> Array.sortBy(fun e -> e.ExpectedSize)
        let totalSize = filesToDownload |> Array.sumBy(fun e -> e.ExpectedSize)
        Console.Write Environment.NewLine
        printfn "Total number of files to download = %d (size = %s), number of failed files = %d" sum (formatFileSize totalSize) invalidFiles.Length
        if sum = 0 then
          if continueDownload invalidFiles plan then
            return! loop ()
          else
            return false
        else if tries > 500 then
          return false
        else 
          do! downloadFilesInChunks filesToDownload plan
          do! Async.Sleep(10000)
          return! loop () 
        
      with
      |_ as e -> 
        failwith $"Error in file downloading: {e.Message}"
        return false   }
    loop ()

let readDownloadPlan path =
  let json = File.ReadAllText(path)
  JsonSerializer.Deserialize<DownloadPlan>(json)

let startProgram plan =
  Console.WriteLine("\nMake sure to review the download plan before proceeding...")
  let planDesc = sprintf "%A" plan
  Console.WriteLine($"\nCurrent download plan in use:\n{planDesc}")
  
  let mutable cont = true  
  while cont do
    Console.WriteLine("\nPress [C] key to download missing files")
    Console.WriteLine("Press [V] key to verify downloads including a summary")
    Console.WriteLine("Press [Esc] key to stop program\n")
    let keyInfo = Console.ReadKey(true)
    if keyInfo.Key = ConsoleKey.C then
      printfn "Press a key to confirm downloads"
      let _ = Console.ReadKey()
      let resultMessage = downloadVerificationLoop plan |> Async.RunSynchronously
      if resultMessage then
        printfn "All files for the given plan successfully downloaded"
      else
        printfn "Errors occured during download session"
    if keyInfo.Key = ConsoleKey.V then
      verify plan
    if keyInfo.Key = ConsoleKey.Escape then
      cont <- false

  printfn "Application ended"

[<EntryPoint>]
let main args =
  
  //for debugging at the moment
  match args with
  |[||] -> 
    startProgram defaultPlan
  |[|arg1|] -> 
    Console.WriteLine($"Received args={arg1}") 
    let fileInfo = FileInfo(arg1)
    let planFromFile =
      if fileInfo.Exists then
        let jsonPlan = readDownloadPlan arg1
        //progress update is not fully working yet
        let jsonPlan = { jsonPlan with EnableProgressUpdate = false; CTS = new CancellationTokenSource() }
        startProgram jsonPlan
      else
        startProgram defaultPlan
    printfn "Received args=%s: %A" arg1 planFromFile

  |[|arg1;arg2|] -> printfn "args: %s %s" arg1 arg2
  |_ -> printfn "Too many args provided"
  
    
  // Return 0 to indicate success
  0