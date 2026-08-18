/**
 * @file interface.h
 * @brief Minimal stub for HL2SDK interface.h
 *
 * The real interface.h from HL2SDK contains various macros for defining plugin interfaces.
 * For mock builds on non-Windows platforms, we only need the abstract_class macro.
 */

#pragma once

// Used by cdll_interfaces.h to mark the pure virtual engine interfaces.
#ifndef abstract_class
#define abstract_class class
#endif
