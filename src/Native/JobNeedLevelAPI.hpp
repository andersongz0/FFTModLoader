#pragma once

#include <cstddef>

namespace fftml
{
    bool InitializeJobNeedLevelAPI();

    std::size_t AppliedJobRequirementPatchCount();
    bool JobNeedLevelTableFound();
}
