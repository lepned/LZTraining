open System
open System.IO
open System.Threading
open LC0Training.GamesDownloader
open System.Text.Json



//Console.Clear()
//for i = 0 to 100 do
  //Console.BufferWidth <- 1000 //Console.WindowWidth + 5
  //Console.BufferHeight <- 1000 //Console.WindowHeight + 1000
  //Console.SetWindowPosition(0,0)
  //let maxCursorValue = min (Console.WindowHeight - 1) Console.CursorTop
  //Console.SetCursorPosition(0,maxCursorValue)
  //printConsoleValues()
  //printfn "Processing number %d" i
  //printConsoleValues()
  //printfn ""

let defaultPlan =  
  {
    StartDate = DateTime(2022,11,1)
    DurationInDays = 1
    Url = "https://storage.lczero.org/files/training_data/test80"
    TargetDir= "E:/LZGames/T80"
    MaxDownloads = 2 // max number of downloads is limited to 10
    AutomaticRetries = true
    CTS = new CancellationTokenSource()
  }

let verify plan =
  printfn "Verification started..."
  let passed = createVerificationSummary plan None //(Some "Dec22.txt")
  if passed then 
    printfn "Verification passed" 
  else 
    printfn "Verification failed - look at your bin dir for summary"

  if not passed then
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
  let rec loop () = async { 
    
    let! resumedFiles = collectAllFilesThatNeedToBeResumed plan
    let! newFiles = collectAllNewFiles plan
    let sum = resumedFiles.Length + newFiles.Length
    printfn "Total number of files to download = %d" sum
    if sum = 0 then
      return true
    else        
      do! downloadResumedFilesInChunks resumedFiles plan 
      do! downloadNewFilesInChunks newFiles plan
      return! loop () }
  loop ()

let readDownloadPlan path =
  let json = File.ReadAllText(path)
  JsonSerializer.Deserialize<DownloadPlan>(json)


let startProgram plan =
  let mutable cont = true  
  while cont do
    Console.WriteLine("\nPress C key to download missing files")
    Console.WriteLine("Press V key to verify downloads including a summary")
    Console.WriteLine("Press Esc key to stop program\n")
    let keyInfo = Console.ReadKey(true)
    if keyInfo.Key = ConsoleKey.C then
      printfn "Press a key to confirm downloads"
      let _ = Console.ReadKey()
      let resultMessage = downloadVerificationLoop plan |> Async.RunSynchronously
      if resultMessage then
        printfn "All files for the given plan successfully downloaded"
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
    Console.WriteLine("\nBe sure to define a download plan before proceeding by open program.fs to adjust settings...")    
    startProgram defaultPlan
  |[|one|] -> 
    let fileInfo = FileInfo(one)
    let planFromFile =
      if fileInfo.Exists then
        let jsonPlan = readDownloadPlan one
        let jsonPlan = {jsonPlan with CTS = new CancellationTokenSource() }
        startProgram jsonPlan
      else
        startProgram defaultPlan
    printfn "Received args=%s: %A" one planFromFile

  |[|one;two|] -> printfn "args: %s %s" one two
  |_ -> printfn "Too many args provided"

  
    
  // Return 0 to indicate success
  0