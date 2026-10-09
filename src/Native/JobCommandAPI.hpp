#pragma once

#include <cstddef>

namespace fftml
{
    bool InitializeJobCommandAPI();

    std::size_t AppliedJobCommandPatchCount();
    bool JobCommandBaseTableFound();
    bool JobCommandWotlTableFound();
}
