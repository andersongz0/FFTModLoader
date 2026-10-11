#pragma once
#include <windows.h>
#include <iostream>

// Classic Windows Quick Edit suspends console output while text is selected.
// Do not let an accidental click stop the launcher's live log polling.
inline DWORD LiveConsoleInputMode(DWORD mode)
{
    return (mode | ENABLE_EXTENDED_FLAGS) & ~ENABLE_QUICK_EDIT_MODE;
}
inline void ConfigureLiveLauncherConsole()
{
    std::wcout.setf(std::ios::unitbuf);
    std::wcerr.setf(std::ios::unitbuf);
    DWORD mode=0;
    HANDLE input=GetStdHandle(STD_INPUT_HANDLE);
    if(input!=INVALID_HANDLE_VALUE && GetConsoleMode(input,&mode))
        SetConsoleMode(input,LiveConsoleInputMode(mode));
}
