# LZTraining
Download training data from LC0

This is a console-based application for downloading training data from storage.lczero.org. 

The configuration/download plan is defined in a json-file like this:
```
{
  "StartDate": "2022-11-01",
  "DurationInDays": 5,
  "Url": "https://storage.lczero.org/files/training_data/test80",
  "TargetDir": "E:/LZGames/T80",
  "MaxDownloads": 10,
  "AutomaticRetries": true,
  "AllowToDeleteFailedFiles": false
}
```
The configuration plan is used by the application to define the above settings. Once the configuration plan is set up, it can be used to download the training data.

In the startup code, there is a defaultPlan variable that defines these settings, which is then used by startProgram as the default configuration plan. If any arguments are provided upon running the program, the program reads these from a JSON file.

After running the program, there are currently three options: downloading missing files by pressing the 'C' key, verifying downloaded files by pressing the 'V' key and stopping the program by pressing the 'Esc' key.
If no arguments are provided upon running the program, then it uses the default configuration plan. If an argument is passed to the program, it is treated as a path to a JSON file containing a custom configuration plan. Upon verifying the downloaded files, there is either a message confirming the successful downloads or a summary of errors that occurred during the download. The program ends after the 'Esc' key is pressed or if there are too many arguments provided.
