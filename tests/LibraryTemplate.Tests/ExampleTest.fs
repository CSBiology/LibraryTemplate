module ExampleTest

open Expecto

[<Tests>]
let exampleTests = 
    testList "Examples" [
        test "Expect true" {
            Expect.equal 42 42 "Example test =)"
        }
    ]
